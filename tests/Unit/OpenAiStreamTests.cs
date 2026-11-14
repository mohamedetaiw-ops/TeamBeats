using System.Net;
using System.Text;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.OpenAiCompatible;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 020 T021: a streamed model reply carries its tool calls in pieces, which come back whole in the final part.</summary>
public sealed class OpenAiStreamTests
{
    private static HttpResponseMessage Sse(params string[] chunks) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };

    private static async Task<List<ProviderStreamPart>> ReadAsync(HttpResponseMessage response)
    {
        var parts = new List<ProviderStreamPart>();
        await foreach (var part in OpenAiWire.ReadStreamAsync(response, CancellationToken.None, CancellationToken.None, "test"))
        {
            parts.Add(part);
        }
        return parts;
    }

    [Fact]
    public async Task Tool_call_pieces_are_joined_by_index()
    {
        var parts = await ReadAsync(Sse(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"knowledge__search","arguments":""}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"query\":"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call-2","function":{"name":"tasks__inbox","arguments":"{}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"leave\"}"}}]},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":""},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":4}}"""));
        var final = Assert.Single(parts).Final!;
        Assert.Equal("tool_calls", final.FinishReason);
        Assert.Collection(final.ToolCalls,
            c => Assert.Equal(("call-1", "knowledge__search", "{\"query\":\"leave\"}"), (c.Id, c.Name, c.Arguments)),
            c => Assert.Equal(("call-2", "tasks__inbox", "{}"), (c.Id, c.Name, c.Arguments)));
    }

    [Fact]
    public async Task Text_streams_as_deltas_and_a_reply_without_tools_has_none()
    {
        var parts = await ReadAsync(Sse(
            """{"choices":[{"index":0,"delta":{"content":"Hello "},"finish_reason":null}]}""",
            """{"choices":[{"index":0,"delta":{"content":"there"},"finish_reason":"stop"}]}"""));
        Assert.Equal(["Hello ", "there"], parts.Where(p => p.Delta is not null).Select(p => p.Delta));
        Assert.Empty(parts.Last().Final!.ToolCalls);
    }
}
