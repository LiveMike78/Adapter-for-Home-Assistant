using System;
using System.Globalization;
using System.Text.Json;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Converts raw HA `state` strings and attribute JsonElements into typed point values.
    /// HA always sends `state` as a string over the wire (even for numeric sensors), so numeric
    /// parsing is attempted first; non-numeric/categorical states fall back to string.
    /// </summary>
    public static class HomeAssistantValueConverter
    {
        private static readonly string[] UnavailableStates = { "unknown", "unavailable", "none" };

        public static bool IsUnavailable(string? rawState) =>
            rawState is null || Array.IndexOf(UnavailableStates, rawState.Trim().ToLowerInvariant()) >= 0;

        /// <summary>
        /// Infers a value type for a HA state string. Booleans are recognized for the common
        /// on/off-style domains; everything else is numeric-if-parseable, else string.
        /// </summary>
        public static HomeAssistantValueType InferStateValueType(string entityId, string rawState)
        {
            if (double.TryParse(rawState, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return HomeAssistantValueType.Double;
            }

            var normalized = rawState.Trim().ToLowerInvariant();
            if (normalized is "on" or "off" or "true" or "false" or "home" or "not_home" or "open" or "closed" or "locked" or "unlocked")
            {
                return HomeAssistantValueType.Boolean;
            }

            return HomeAssistantValueType.String;
        }

        public static HomeAssistantPointValue ConvertState(
            string pointId, string streamId, string entityId, string rawState, DateTimeOffset timestamp, HomeAssistantValueType valueType)
        {
            if (IsUnavailable(rawState))
            {
                return new HomeAssistantPointValue
                {
                    PointId = pointId,
                    StreamId = streamId,
                    Timestamp = timestamp,
                    ValueType = valueType,
                    IsUnavailable = true
                };
            }

            return valueType switch
            {
                HomeAssistantValueType.Double when double.TryParse(rawState, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) =>
                    new HomeAssistantPointValue { PointId = pointId, StreamId = streamId, Timestamp = timestamp, ValueType = valueType, DoubleValue = d },

                HomeAssistantValueType.Boolean =>
                    new HomeAssistantPointValue
                    {
                        PointId = pointId,
                        StreamId = streamId,
                        Timestamp = timestamp,
                        ValueType = valueType,
                        BooleanValue = ToBoolean(rawState)
                    },

                _ => new HomeAssistantPointValue { PointId = pointId, StreamId = streamId, Timestamp = timestamp, ValueType = HomeAssistantValueType.String, StringValue = rawState }
            };
        }

        private static bool ToBoolean(string rawState) => rawState.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "home" or "open" or "unlocked" => true,
            _ => false
        };

        /// <summary>
        /// Determines whether a HA attribute value is "scalar" (number/string/bool) and therefore
        /// eligible to become its own selectable point under the MVP default (see prep notes §5.2).
        /// Arrays/objects are excluded unless FlattenSimpleArrayAttributes handling is added later.
        /// </summary>
        public static bool TryConvertAttribute(
            string pointId, string streamId, JsonElement element, DateTimeOffset timestamp, out HomeAssistantPointValue? value, out HomeAssistantValueType valueType)
        {
            value = null;
            valueType = HomeAssistantValueType.String;

            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    valueType = HomeAssistantValueType.Double;
                    value = new HomeAssistantPointValue
                    {
                        PointId = pointId,
                        StreamId = streamId,
                        Timestamp = timestamp,
                        ValueType = valueType,
                        DoubleValue = element.GetDouble()
                    };
                    return true;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    valueType = HomeAssistantValueType.Boolean;
                    value = new HomeAssistantPointValue
                    {
                        PointId = pointId,
                        StreamId = streamId,
                        Timestamp = timestamp,
                        ValueType = valueType,
                        BooleanValue = element.GetBoolean()
                    };
                    return true;

                case JsonValueKind.String:
                    valueType = HomeAssistantValueType.String;
                    value = new HomeAssistantPointValue
                    {
                        PointId = pointId,
                        StreamId = streamId,
                        Timestamp = timestamp,
                        ValueType = valueType,
                        StringValue = element.GetString()
                    };
                    return true;

                case JsonValueKind.Null:
                    valueType = HomeAssistantValueType.String;
                    value = new HomeAssistantPointValue
                    {
                        PointId = pointId,
                        StreamId = streamId,
                        Timestamp = timestamp,
                        ValueType = valueType,
                        IsUnavailable = true
                    };
                    return true;

                // Arrays/objects: not scalar - excluded from MVP point set (see prep notes §5.2).
                case JsonValueKind.Array:
                case JsonValueKind.Object:
                default:
                    return false;
            }
        }
    }
}
