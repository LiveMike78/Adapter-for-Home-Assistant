using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Applies the DataSourceConfiguration include/exclude domain and entity_id rules
    /// (see prep notes §5.3). Precedence: IncludeDomains/ExcludeDomains first, then
    /// IncludeEntities (if any given, entity must match one pattern), then ExcludeEntities.
    /// </summary>
    public class EntityFilter
    {
        private readonly DataSourceConfiguration _config;

        public EntityFilter(DataSourceConfiguration config)
        {
            _config = config;
        }

        public bool IsEntityIncluded(string entityId)
        {
            var domain = GetDomain(entityId);

            if (_config.IncludeDomains.Count > 0 && !_config.IncludeDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                return false;

            if (_config.ExcludeDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                return false;

            if (_config.IncludeEntities.Count > 0 && !_config.IncludeEntities.Any(p => GlobMatch(p, entityId)))
                return false;

            if (_config.ExcludeEntities.Any(p => GlobMatch(p, entityId)))
                return false;

            return true;
        }

        public bool IsAttributeIncluded(string attributeName) =>
            !_config.ExcludeAttributes.Contains(attributeName, StringComparer.OrdinalIgnoreCase);

        public static string GetDomain(string entityId)
        {
            var idx = entityId.IndexOf('.');
            return idx < 0 ? entityId : entityId[..idx];
        }

        /// <summary>Simple '*' wildcard glob match, case-insensitive.</summary>
        public static bool GlobMatch(string pattern, string value)
        {
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
        }
    }
}
