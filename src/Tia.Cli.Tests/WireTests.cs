using System.Text.Json;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    public class WireTests
    {
        [Fact]
        public void ResponseOmitsNullFields()
        {
            var json = JsonSerializer.Serialize(
                new WireResponse { Id = 7, Ok = true }, WireJson.Options);
            Assert.Contains("\"id\":7", json);
            Assert.Contains("\"ok\":true", json);
            Assert.DoesNotContain("error", json);
            Assert.DoesNotContain("result", json);
        }

        [Fact]
        public void WireJsonStaysOnOneLine()
        {
            // The pipe frames messages with newlines; an indented payload would desync the reader.
            var response = new WireResponse
            {
                Id = 1,
                Ok = false,
                Error = new WireError
                {
                    Code = "not_found",
                    Message = "line one",
                    Detail = "with detail",
                    Hint = "and hint",
                },
            };
            var json = JsonSerializer.Serialize(response, WireJson.Options);
            Assert.DoesNotContain("\n", json);
            Assert.DoesNotContain("\r", json);
        }

        [Fact]
        public void RequestRoundTripsThroughTheWireFormat()
        {
            var sent = new WireRequest
            {
                Id = 42,
                Method = "blocks.list",
                Params = JsonUtil.ToElement(new { deviceName = "PLC_1", filter = "Motor" }),
            };

            var received = JsonSerializer.Deserialize<WireRequest>(
                JsonSerializer.Serialize(sent, WireJson.Options), WireJson.Options);

            Assert.Equal(42, received.Id);
            Assert.Equal("blocks.list", received.Method);
            var p = new Params(received.Params);
            Assert.Equal("PLC_1", p.RequiredString("deviceName"));
            Assert.Equal("Motor", p.String("filter"));
        }

        [Fact]
        public void ErrorRoundTripsAllFields()
        {
            var json = JsonSerializer.Serialize(new WireError
            {
                Code = "access_denied",
                Message = "m",
                Detail = "d",
                Hint = "h",
            }, WireJson.Options);
            var back = JsonSerializer.Deserialize<WireError>(json, WireJson.Options);
            Assert.Equal("access_denied", back.Code);
            Assert.Equal("m", back.Message);
            Assert.Equal("d", back.Detail);
            Assert.Equal("h", back.Hint);
        }

        [Fact]
        public void ToElementSurvivesItsSourceDocument()
        {
            // JsonUtil.ToElement clones, so the element must stay readable long after the
            // JsonDocument it came from is disposed.
            var element = JsonUtil.ToElement(new { name = "PLC_1", number = 1 });
            Assert.Equal("PLC_1", element.GetProperty("name").GetString());
            Assert.Equal(1, element.GetProperty("number").GetInt32());
        }
    }

    public class ParamsTests
    {
        private static Params Of(object value) => new Params(JsonUtil.ToElement(value));

        [Fact]
        public void MissingParamsObjectYieldsFallbacks()
        {
            var p = new Params(null);
            Assert.Null(p.String("x"));
            Assert.True(p.Bool("x", true));
            Assert.Equal(5, p.Int("x", 5));
            Assert.Null(p.NullableBool("x"));
            Assert.Null(p.NullableInt("x"));
        }

        [Fact]
        public void RequiredStringThrowsInvalidRequestWhenMissingOrBlank()
        {
            var ex = Assert.Throws<WireException>(() => Of(new { }).RequiredString("name"));
            Assert.Equal(WireErrorCodes.InvalidRequest, ex.Code);

            Assert.Throws<WireException>(() => Of(new { name = "  " }).RequiredString("name"));
        }

        [Fact]
        public void NullableBoolSeparatesUnsaidFromFalse()
        {
            Assert.Null(Of(new { }).NullableBool("flag"));
            Assert.False(Of(new { flag = false }).NullableBool("flag"));
            Assert.True(Of(new { flag = true }).NullableBool("flag"));
        }

        [Fact]
        public void WrongKindFallsBackInsteadOfThrowing()
        {
            var p = Of(new { count = "many", name = 3 });
            Assert.Equal(9, p.Int("count", 9));
            Assert.Null(p.String("name"));
        }

        [Fact]
        public void ExplicitNullCountsAsAbsent()
        {
            var p = Of(new { name = (string)null });
            Assert.Null(p.String("name"));
            Assert.Throws<WireException>(() => p.RequiredString("name"));
        }
    }
}
