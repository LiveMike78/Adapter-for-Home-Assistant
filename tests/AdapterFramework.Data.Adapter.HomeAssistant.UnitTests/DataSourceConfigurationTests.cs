using AdapterFramework.Data.Adapter.HomeAssistant;
using Xunit;

namespace AdapterFramework.Data.Adapter.HomeAssistant.UnitTests
{
    public class DataSourceConfigurationTests
    {
        [Fact]
        public void Validate_RequiresBaseUrlAndAccessToken()
        {
            var config = new DataSourceConfiguration();
            var errors = config.Validate();

            Assert.Contains(errors, e => e.Contains("BaseUrl"));
            Assert.Contains(errors, e => e.Contains("AccessToken"));
        }

        [Fact]
        public void Validate_RejectsInsecureUrlByDefault()
        {
            var config = new DataSourceConfiguration
            {
                BaseUrl = "http://homeassistant.local:8123",
                AccessToken = "token",
                AllowInsecureBaseUrl = false
            };

            var errors = config.Validate();

            Assert.Contains(errors, e => e.Contains("https"));
        }

        [Fact]
        public void Validate_AllowsInsecureUrlWhenExplicitlyEnabled()
        {
            var config = new DataSourceConfiguration
            {
                BaseUrl = "http://homeassistant.local:8123",
                AccessToken = "token",
                AllowInsecureBaseUrl = true
            };

            var errors = config.Validate();

            Assert.Empty(errors);
        }

        [Theory]
        [InlineData("http://homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
        [InlineData("https://ha.example.com", "wss://ha.example.com/api/websocket")]
        public void GetEffectiveWebSocketUrl_DerivesFromBaseUrl(string baseUrl, string expected)
        {
            var config = new DataSourceConfiguration { BaseUrl = baseUrl, AccessToken = "t", AllowInsecureBaseUrl = true };

            Assert.Equal(expected, config.GetEffectiveWebSocketUrl());
        }

        [Fact]
        public void GetEffectiveWebSocketUrl_PrefersExplicitOverride()
        {
            var config = new DataSourceConfiguration
            {
                BaseUrl = "http://homeassistant.local:8123",
                WebSocketUrl = "ws://override:1234/api/websocket",
                AccessToken = "t",
                AllowInsecureBaseUrl = true
            };

            Assert.Equal("ws://override:1234/api/websocket", config.GetEffectiveWebSocketUrl());
        }
    }

    public class EntityFilterTests
    {
        [Fact]
        public void IsEntityIncluded_RespectsIncludeDomains()
        {
            var config = new DataSourceConfiguration
            {
                IncludeDomains = new() { "sensor" },
                ExcludeDomains = new()
            };
            var filter = new EntityFilter(config);

            Assert.True(filter.IsEntityIncluded("sensor.kitchen_temperature"));
            Assert.False(filter.IsEntityIncluded("automation.morning_lights"));
        }

        [Fact]
        public void IsEntityIncluded_ExcludeEntitiesOverridesIncludeDomains()
        {
            var config = new DataSourceConfiguration
            {
                IncludeDomains = new() { "sensor" },
                ExcludeDomains = new(),
                ExcludeEntities = new() { "sensor.debug_*" }
            };
            var filter = new EntityFilter(config);

            Assert.False(filter.IsEntityIncluded("sensor.debug_internal"));
            Assert.True(filter.IsEntityIncluded("sensor.kitchen_temperature"));
        }

        [Fact]
        public void IsAttributeIncluded_RespectsExcludeList()
        {
            var config = new DataSourceConfiguration { ExcludeAttributes = new() { "icon" } };
            var filter = new EntityFilter(config);

            Assert.False(filter.IsAttributeIncluded("icon"));
            Assert.True(filter.IsAttributeIncluded("battery_level"));
        }

        [Theory]
        [InlineData("sensor.kitchen_*", "sensor.kitchen_temperature", true)]
        [InlineData("sensor.kitchen_*", "sensor.living_room_temperature", false)]
        [InlineData("light.bed_light", "light.bed_light", true)]
        public void GlobMatch_Works(string pattern, string value, bool expected)
        {
            Assert.Equal(expected, EntityFilter.GlobMatch(pattern, value));
        }
    }

    public class HomeAssistantDataSelectionItemTests
    {
        [Fact]
        public void Selected_DefaultsFalse()
        {
            var item = new HomeAssistantDataSelectionItem();
            Assert.False(item.Selected);
        }

        [Fact]
        public void GetEffectiveStreamId_FallsBackToPointIdWhenStreamIdNotSet()
        {
            var item = new HomeAssistantDataSelectionItem { PointId = "sensor.kitchen_temperature" };
            Assert.Equal("sensor.kitchen_temperature", item.GetEffectiveStreamId());
        }

        [Fact]
        public void GetEffectiveStreamId_UsesExplicitStreamIdWhenSet()
        {
            var item = new HomeAssistantDataSelectionItem
            {
                PointId = "light.bed_light",
                StreamId = "BedroomLights.BedLight.State"
            };
            Assert.Equal("BedroomLights.BedLight.State", item.GetEffectiveStreamId());
        }
    }

    public class HomeAssistantValueConverterTests
    {
        [Theory]
        [InlineData("21.4", HomeAssistantValueType.Double)]
        [InlineData("on", HomeAssistantValueType.Boolean)]
        [InlineData("off", HomeAssistantValueType.Boolean)]
        [InlineData("sunny", HomeAssistantValueType.String)]
        public void InferStateValueType_Works(string rawState, HomeAssistantValueType expected)
        {
            Assert.Equal(expected, HomeAssistantValueConverter.InferStateValueType("sensor.x", rawState));
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("unavailable")]
        [InlineData(null)]
        public void IsUnavailable_DetectsSpecialStates(string? rawState)
        {
            Assert.True(HomeAssistantValueConverter.IsUnavailable(rawState));
        }

        [Fact]
        public void IsUnavailable_FalseForNormalState()
        {
            Assert.False(HomeAssistantValueConverter.IsUnavailable("21.4"));
        }
    }
}
