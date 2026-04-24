using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MxPlot.UI.Avalonia.Analysis
{
    /// <summary>
    /// Central registry for <see cref="IProfileFitter"/> implementations.
    /// Pre-loaded with <see cref="GaussianProfileFitter"/>; additional fitters
    /// can be added at runtime via <see cref="Register"/> or discovered from
    /// external DLLs via <see cref="LoadFromDirectory"/>.
    /// </summary>
    public static class ProfileFitterRegistry
    {
        private static readonly List<IProfileFitter> _fitters =
        [
            GaussianProfileFitter.Instance,
            LorentzianProfileFitter.Instance,
        ];

        /// <summary>All currently registered fitters (read-only snapshot).</summary>
        public static IReadOnlyList<IProfileFitter> Fitters => _fitters.AsReadOnly();

        /// <summary>Fired whenever the fitter list changes.</summary>
        public static event Action? FittersChanged;

        /// <summary>Registers a fitter programmatically.</summary>
        public static void Register(IProfileFitter fitter)
        {
            _fitters.Add(fitter);
            FittersChanged?.Invoke();
        }

        /// <summary>
        /// Scans <paramref name="directory"/> for DLLs, instantiates every exported
        /// class that implements <see cref="IProfileFitter"/>, and registers it.
        /// Malformed or incompatible DLLs are silently skipped.
        /// </summary>
        public static void LoadFromDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var dll in Directory.GetFiles(directory, "*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (type.IsClass && !type.IsAbstract
                            && typeof(IProfileFitter).IsAssignableFrom(type)
                            && Activator.CreateInstance(type) is IProfileFitter fitter)
                        {
                            Register(fitter);
                        }
                    }
                }
                catch { /* skip unloadable / incompatible DLLs */ }
            }
        }
    }
}
