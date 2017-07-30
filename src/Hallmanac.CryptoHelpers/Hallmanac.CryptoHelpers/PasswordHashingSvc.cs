﻿using System;
using System.Security.Cryptography;
using System.Text;

using Funqy.CSharp;


namespace Hallmanac.CryptoHelpers
{
    /// <summary>
    /// Provides strong hashing services using using the standards from RFC2898 with key stretching and multiple hashing iterations on a SHA512 algorthim.
    /// </summary>
    public class PasswordHashingSvc : IPasswordHashingSvc
    {
        private const int MinIterationRange = 8000;
        private const int MaxIterationRange = 15000;
        private const int MinSaltSize = 64;
        private const int MaxSaltSize = 96;
        private const int MinPasswordSize = 8;
        private const int MaxPasswordSize = 1024;
        private const int AppHashIterations = 6000;
        private const int KeyLength = 64;

        private readonly string _globalApplicationSalt;


        /// <summary>
        /// Creates a new instance of the PasswordEncryptionSvc. If the application has a global application salt, then pass 
        /// that into this constructor otherwise the hashing will not match up.
        /// </summary>
        /// <param name="globalApplicationSalt">The global application Salt is used to do an initial hash of the password
        /// and then do a normal salt and hash of the password. The global app salt is kept secret inside Azure Key Vault
        /// </param>
        public PasswordHashingSvc(string globalApplicationSalt = null)
        {
            _globalApplicationSalt = globalApplicationSalt;
        }


        /// <summary>
        /// Generates a random byte array key based on the byte length given and returns it as a hexadecimal string.
        /// </summary>
        /// <param name="byteLength">Length of Byte array used in the random generator</param>
        /// <returns>Hexadecimal text representation of the randomly generated bytes.</returns>
        public string GenerateHexKeyFromByteLength(int byteLength)
        {
            var key = new byte[byteLength];
            GenerateRandomBytes(key);
            return key.ToHexString();
        }


        /// <summary>
        /// Computes a hash for a given password and returns the string representation for that
        /// </summary>
        /// <param name="givenPassword"></param>
        public FunqResult<PasswordHashingData> HashPassword(string givenPassword)
        {
            if (string.IsNullOrWhiteSpace(givenPassword))
            {
                return FunqFactory.Fail("The given password was null or empty", (PasswordHashingData) null);
            }

            var hashData = new PasswordHashingData();
            var rand = new Random();

            // Set the hash data
            hashData.NumberOfIterations = rand.Next(MinIterationRange, MaxIterationRange);
            hashData.SaltSize = rand.Next(MinSaltSize, MaxSaltSize);
            var saltByteArray = GetRandomSalt(hashData.SaltSize);
            hashData.Salt = saltByteArray.ToHexString();

            // Run initial hash at an application level
            var appHashedPassword = GetAppLevelPasswordHash(givenPassword);

            // Take the output of the inital hashing and run it through proper hashing with key stretching
            var hashedPasswordResult =
                ComputePasswordAndSaltBytes(saltByteArray, appHashedPassword, hashData.NumberOfIterations)
                    .Then(computedBytes =>
                    {
                        var hashedPassword = computedBytes.ToHexString();
                        return FunqFactory.Ok<string>(hashedPassword);
                    });
            if (hashedPasswordResult.IsFailure)
            {
                return FunqFactory.Fail(hashedPasswordResult.Message, hashData);
            }
            hashData.HashedPassword = hashedPasswordResult.Value;
            return FunqFactory.Ok(hashData);
        }


        /// <summary>
        /// This will compute a hash for the given password and salt using the iterationCount parameter to determine how many times the
        /// hashing will occur on the password + salt.
        /// </summary>
        /// <param name="salt">The salt that is added on to the end of the password</param>
        /// <param name="password">The password to be hashed</param>
        /// <param name="iterationCount">The number of times the password+salt will have a hash computed</param>
        /// <returns></returns>
        public FunqResult<byte[]> ComputePasswordAndSaltBytes(byte[] salt, string password, int iterationCount = MinIterationRange)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                return FunqFactory.Fail("Password was null", (byte[]) null);
            }
            if (salt == null || salt.Length < 1)
            {
                return FunqFactory.Fail("The salt did not meet the minimum length", (byte[]) null);
            }
            if (salt.Length > MaxSaltSize)
            {
                return FunqFactory.Fail("The salt length was greater than the maximum allowed", (byte[]) null);
            }

            var convertedPassword = password.ToUtf8Bytes();
            if (convertedPassword.Length > MaxPasswordSize)
            {
                return FunqFactory.Fail($"The password length was greater than the maximum allowed. Please make it less than {MaxPasswordSize}",
                                        (byte[]) null);
            }

            if (convertedPassword.Length < MinPasswordSize)
            {
                return FunqFactory.Fail($"The password length was less than the minimum allowed. Please make it greater than {MinPasswordSize}",
                                        (byte[]) null);
            }

            try
            {
                var resultValue = new Rfc2898(convertedPassword, salt, iterationCount).GetDerivedKeyBytes_PBKDF2_HMACSHA512(KeyLength);
                return FunqFactory.Ok(resultValue);
            }
            catch (IterationsLessThanRecommended)
            {
                return FunqFactory.Fail("The number of hash iterations did not meet minimum standards", (byte[]) null);
            }
            catch (SaltLessThanRecommended)
            {
                return FunqFactory.Fail("The salt length is less than the minimum standards", (byte[]) null);
            }
            catch (Exception e)
            {
                return FunqFactory.Fail($"There was an unspecified error that ocurred from an exception being thrown. The exception is as follows:\n{e.Message}", (byte[])null);
            }
        }


        /// <summary>
        /// Generates a random byte array with a length that is equivalent to the given "saltLength" parameter.
        /// </summary>
        /// <param name="saltLength">The length of the salt to be generated.</param>
        public byte[] GetRandomSalt(int saltLength)
        {
            var salt = new byte[saltLength];
            RandomNumberGenerator.Create().GetBytes(salt);
            return salt;
        }


        /// <summary>
        /// Hashes the given plain-text password using the global application hash. If there is no global application hash then the given password 
        /// is returned simply hashed without the global app salt.
        /// </summary>
        /// <param name="givenPassword"></param>
        public string GetAppLevelPasswordHash(string givenPassword)
        {
            if (string.IsNullOrWhiteSpace(givenPassword))
            {
                return givenPassword;
            }

            if (string.IsNullOrWhiteSpace(_globalApplicationSalt))
            {
                var hash512Password = ComputeSha512ToHexString(givenPassword);
                return hash512Password;
            }
            var appSaltByteArray = _globalApplicationSalt.ToHexBytes();
            var hashedPasswordResult = ComputePasswordAndSaltBytes(appSaltByteArray, givenPassword, AppHashIterations)
                .Then(byteResult =>
                {
                    var hashedPassword = byteResult.ToHexString();
                    return FunqFactory.Ok<string>(hashedPassword);
                });
            return hashedPasswordResult.IsFailure ? givenPassword : hashedPasswordResult.Value;
        }


        /// <summary>
        /// Compares a given (plain text) password with the (already hashed) password that is inside the hashData object.
        /// </summary>
        /// <param name="givenPassword">Plain text password to compare with</param>
        /// <param name="hashData">
        /// The <see cref="PasswordHashingData"/> object that contains salt size, current hashed password, etc. for use in the comparison
        /// of the two passwords
        /// </param>
        public FunqResult ComparePasswords(string givenPassword, PasswordHashingData hashData)
        {
            if (string.IsNullOrWhiteSpace(givenPassword) || string.IsNullOrWhiteSpace(hashData?.HashedPassword) || string.IsNullOrWhiteSpace(hashData.Salt))
            {
                return FunqFactory.Fail("The given data to compare passwords was invalid.", false);
            }

            var saltByteArray = hashData.Salt.ToHexBytes();

            // Run initial hash at an application level
            var appHashedPassword = GetAppLevelPasswordHash(givenPassword);

            // Take the output of the inital hashing and run it through proper hashing with key stretching
            var hashedPasswordResult =
                ComputePasswordAndSaltBytes(saltByteArray, appHashedPassword, hashData.NumberOfIterations)
                    .Then(computedBytes =>
                    {
                        var hashedPassword = computedBytes.ToHexString();
                        return FunqFactory.Ok<string>(hashedPassword);
                    });
            if (hashedPasswordResult.IsFailure)
            {
                return FunqFactory.Fail($"Computing the hash for the given password was not successful due to the following:\n{hashedPasswordResult.Message}");
            }

            return hashedPasswordResult.Value == hashData.HashedPassword
                ? FunqFactory.Ok()
                : FunqFactory.Fail("Passwords did not match");
        }


        /// <summary>
        /// Generates random, non-zero bytes using the RandomNumberGenerator
        /// </summary>
        /// <param name="buffer">Length of random bytes to be generated.</param>
        public void GenerateRandomBytes(byte[] buffer)
        {
            if (buffer == null)
                return;
            var rng = RandomNumberGenerator.Create();
            rng.GetBytes(buffer);
        }


        /// <summary>
        /// Computes a hash based on the HMACSHA512 algorithm using the given key.
        /// </summary>
        public string ComputeSha512ToHexString(string textToHash)
        {
            if (string.IsNullOrEmpty(textToHash))
                return null;
            var sha512Cng = SHA512.Create();
            var hashBytes = sha512Cng.ComputeHash(Encoding.UTF8.GetBytes(textToHash));
            var hashToHexString = hashBytes.ToHexString();
            return hashToHexString;
        }
    }


    /// <summary>
    /// Provides strong hashing services using using the standards from RFC2898 with key stretching and multiple hashing iterations on a SHA512 algorthim.
    /// </summary>
    public interface IPasswordHashingSvc
    {
        /// <summary>
        /// Compares a given (plain text) password with the (already hashed) password that is inside the hashData object.
        /// </summary>
        /// <param name="givenPassword">Plain text password to compare with</param>
        /// <param name="hashData">
        /// The <see cref="PasswordHashingData"/> object that contains salt size, current hashed password, etc. for use in the comparison
        /// of the two passwords
        /// </param>
        FunqResult ComparePasswords(string givenPassword, PasswordHashingData hashData);

        /// <summary>
        /// This will compute a hash for the given password and salt using the iterationCount parameter to determine how many times the
        /// hashing will occur on the password + salt.
        /// </summary>
        /// <param name="salt">The salt that is added on to the end of the password</param>
        /// <param name="password">The password to be hashed</param>
        /// <param name="iterationCount">The number of times the password+salt will have a hash computed</param>
        /// <returns></returns>
        FunqResult<byte[]> ComputePasswordAndSaltBytes(byte[] salt, string password, int iterationCount = 8000);

        /// <summary>
        /// Computes a hash based on the HMACSHA512 algorithm using the given key.
        /// </summary>
        string ComputeSha512ToHexString(string textToHash);

        /// <summary>
        /// Generates a random byte array key based on the byte length given and returns it as a hexadecimal string.
        /// </summary>
        /// <param name="byteLength">Length of Byte array used in the random generator</param>
        /// <returns>Hexadecimal text representation of the randomly generated bytes.</returns>
        string GenerateHexKeyFromByteLength(int byteLength);

        /// <summary>
        /// Generates random, non-zero bytes using the RandomNumberGenerator
        /// </summary>
        /// <param name="buffer">Length of random bytes to be generated.</param>
        void GenerateRandomBytes(byte[] buffer);

        /// <summary>
        /// Hashes the given plain-text password using the global application hash. If there is no global application hash then the given password 
        /// is returned simply hashed without the global app salt.
        /// </summary>
        /// <param name="givenPassword"></param>
        string GetAppLevelPasswordHash(string givenPassword);

        /// <summary>
        /// Generates a random byte array with a length that is equivalent to the given "saltLength" parameter.
        /// </summary>
        /// <param name="saltLength">The length of the salt to be generated.</param>
        byte[] GetRandomSalt(int saltLength);

        /// <summary>
        /// Computes a hash for a given password and returns the string representation for that
        /// </summary>
        /// <param name="givenPassword"></param>
        FunqResult<PasswordHashingData> HashPassword(string givenPassword);
    }
}