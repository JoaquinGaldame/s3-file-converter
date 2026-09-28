using System;
using System.Collections.Generic;
using System.Linq;

namespace S3FileConverter.Converters
{
    /// <summary>
    /// Registry for file converters. Resolves which converter to use based on input/output formats.
    /// </summary>
    public class ConverterRegistry
    {
        private readonly List<IFileConverter> _converters = new List<IFileConverter>();

        /// <summary>
        /// Register a converter in the registry.
        /// </summary>
        public void Register(IFileConverter converter)
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));

            _converters.Add(converter);
        }

        /// <summary>
        /// Get all registered converters.
        /// </summary>
        public IReadOnlyList<IFileConverter> GetAll() => _converters.AsReadOnly();

        /// <summary>
        /// Find a converter that can handle the given input extension and produces the desired output.
        /// </summary>
        /// <param name="inputExtension">Source file extension (lowercase, without dot)</param>
        /// <param name="outputExtension">Desired output extension (lowercase, without dot)</param>
        /// <returns>Matching converter or null if none found</returns>
        public IFileConverter Find(string inputExtension, string outputExtension)
        {
            if (string.IsNullOrEmpty(inputExtension) || string.IsNullOrEmpty(outputExtension))
                return null;

            inputExtension = inputExtension.ToLowerInvariant();
            outputExtension = outputExtension.ToLowerInvariant();

            return _converters.FirstOrDefault(c =>
                c.CanConvert(inputExtension) &&
                c.OutputExtension.Equals(outputExtension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Find all converters that can handle a given input extension.
        /// </summary>
        public IEnumerable<IFileConverter> FindByInput(string inputExtension)
        {
            if (string.IsNullOrEmpty(inputExtension))
                return Enumerable.Empty<IFileConverter>();

            inputExtension = inputExtension.ToLowerInvariant();
            return _converters.Where(c => c.CanConvert(inputExtension));
        }

        /// <summary>
        /// Find all converters that produce a given output extension.
        /// </summary>
        public IEnumerable<IFileConverter> FindByOutput(string outputExtension)
        {
            if (string.IsNullOrEmpty(outputExtension))
                return Enumerable.Empty<IFileConverter>();

            outputExtension = outputExtension.ToLowerInvariant();
            return _converters.Where(c =>
                c.OutputExtension.Equals(outputExtension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get all supported input extensions across all converters.
        /// </summary>
        public IEnumerable<string> GetSupportedInputExtensions()
        {
            return _converters
                .SelectMany(c => c.SupportedInputExtensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e);
        }

        /// <summary>
        /// Get all supported output extensions across all converters.
        /// </summary>
        public IEnumerable<string> GetSupportedOutputExtensions()
        {
            return _converters
                .Select(c => c.OutputExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e);
        }

        /// <summary>
        /// Create a registry with all built-in converters.
        /// </summary>
        public static ConverterRegistry CreateDefault()
        {
            var registry = new ConverterRegistry();

            // Register built-in converters
            registry.Register(new HeicToJpegConverter());

            // Future converters can be added here:
            // registry.Register(new PngToJpegConverter());
            // registry.Register(new WebpToJpegConverter());

            return registry;
        }
    }
}
