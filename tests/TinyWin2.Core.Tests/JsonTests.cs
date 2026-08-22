using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TinyWin2.Core.Tests;

public sealed class JsonTests {
    [Test]
    public async Task JsonSerializerPolicyIsCamelCaseAndOmitsNulls() {
        var sample = new Sample("n", 3);
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var node = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(sample, options))!;
        await Assert.That(sample.Name).IsEqualTo("n");
        await Assert.That(sample.Count).IsEqualTo(3);
        await Assert.That(sample.Note).IsNull();
        await Assert.That(string.Join(",", node.Select(p => p.Key))).IsEqualTo("name,count");
        await Assert.That(node["name"]!.GetValue<string>()).IsEqualTo("n");
        await Assert.That(node["count"]!.GetValue<int>()).IsEqualTo(3);
    }

    [Test]
    public async Task CjkAndNonAsciiSurviveUnescaped() {
        var node = new JsonObject { ["text"] = "测试 tinywin2 ✓" };
        var compact = node.ToCompactString();
        await Assert.That(compact).Contains("测试 tinywin2 ✓");
        await Assert.That(compact.Contains("\\u")).IsFalse();
    }

    [Test]
    public async Task PrettyAndCompactDifferOnlyInWhitespace() {
        var node = new JsonObject { ["a"] = 1, ["b"] = new JsonArray(1, 2) };
        var pretty = node.ToPrettyString();
        var compact = node.ToCompactString();
        await Assert.That(pretty).Contains("\n  ");
        await Assert.That(compact.Contains('\n')).IsFalse();
        await Assert.That(JsonNode.Parse(pretty)!.ToJsonString())
            .IsEqualTo(JsonNode.Parse(compact)!.ToJsonString());
    }

    private sealed class Sample(string name, int count, string? note = null) {
        public string Name { get; } = name;
        public int Count { get; } = count;
        public string? Note { get; } = note;
    }
}
