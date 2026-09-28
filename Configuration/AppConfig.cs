using System;
using System.Configuration;
using Amazon;

namespace S3FileConverter.Configuration
{
    /// <summary>
    /// Static configuration reader for AWS credentials and S3 settings.
    /// Values are read from App.config.
    /// </summary>
    public static class AppConfig
    {
        public static string AccessKeyId => GetRequiredSetting("AWS:AccessKeyId");
        public static string SecretAccessKey => GetRequiredSetting("AWS:SecretAccessKey");
        public static string RegionName => GetSetting("AWS:Region", "eu-west-1");
        public static RegionEndpoint Region => RegionEndpoint.GetBySystemName(RegionName);

        public static string BucketName => GetSetting("S3:BucketName", "maintenance-attachments.i-rent.net");

        /// <summary>
        /// Validates that all required settings are configured.
        /// Call this at startup to fail fast if credentials are missing.
        /// </summary>
        public static void Validate()
        {
            var accessKey = AccessKeyId;
            var secretKey = SecretAccessKey;

            if (string.IsNullOrWhiteSpace(accessKey) || accessKey.Contains("TU_ACCESS_KEY"))
            {
                throw new ConfigurationErrorsException(
                    "AWS:AccessKeyId is not configured in App.config. " +
                    "Edit S3FileConverter.exe.config and provide your credentials.");
            }

            if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Contains("TU_SECRET_KEY"))
            {
                throw new ConfigurationErrorsException(
                    "AWS:SecretAccessKey is not configured in App.config. " +
                    "Edit S3FileConverter.exe.config and provide your credentials.");
            }
        }

        private static string GetRequiredSetting(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException($"Missing required configuration: {key}");
            }
            return value;
        }

        private static string GetSetting(string key, string defaultValue)
        {
            var value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }
    }
}
