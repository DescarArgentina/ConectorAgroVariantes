using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Web_Service
{
    /// <summary>
    /// Mantiene el mapeo entre:
    /// - Proceso (P-xxxxx) -> PR excluido (PRnnnnnn)
    /// - PR excluido (PRnnnnnn) -> Proceso (P-xxxxx)
    ///
    /// Se usa para que SG2/SH3 pueda colgar el procedimiento en el proceso padre
    /// cuando el PR se "colapsa" en SG1.
    /// </summary>
    public static class Sg1Exclusions
    {
        private static readonly ConcurrentDictionary<string, string> _processToExcludedPr =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string> _excludedPrToProcess =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Clear()
        {
            _processToExcludedPr.Clear();
            _excludedPrToProcess.Clear();
        }

        public static void SetExcluded(string processP, string prToExclude)
        {
            if (string.IsNullOrWhiteSpace(processP) || string.IsNullOrWhiteSpace(prToExclude))
                return;

            processP = processP.Trim();
            prToExclude = prToExclude.Trim();

            // Si ese proceso ya tenía otro PR excluido, lo retiramos del reverse map para evitar basura
            if (_processToExcludedPr.TryGetValue(processP, out var oldPr) &&
                !string.Equals(oldPr, prToExclude, StringComparison.OrdinalIgnoreCase))
            {
                _excludedPrToProcess.TryRemove(oldPr, out _);
            }

            _processToExcludedPr[processP] = prToExclude;
            _excludedPrToProcess[prToExclude] = processP;
        }

        public static bool TryGetExcludedPrForProcess(string processP, out string prToExclude)
        {
            prToExclude = null;
            if (string.IsNullOrWhiteSpace(processP)) return false;
            return _processToExcludedPr.TryGetValue(processP.Trim(), out prToExclude);
        }

        public static bool TryGetProcessForExcludedPr(string prToExclude, out string processP)
        {
            processP = null;
            if (string.IsNullOrWhiteSpace(prToExclude)) return false;
            return _excludedPrToProcess.TryGetValue(prToExclude.Trim(), out processP);
        }

        // Opcional para debug
        public static IReadOnlyDictionary<string, string> SnapshotProcessToExcludedPr()
            => new Dictionary<string, string>(_processToExcludedPr, StringComparer.OrdinalIgnoreCase);
    }
}
