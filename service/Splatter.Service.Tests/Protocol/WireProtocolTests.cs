// Wire-protocol contract tests.
// These pin the exact JSON shape the Unity client must produce and consume,
// and guard the canonical ProtocolJson.Options (camelCase, string enums,
// case-insensitive reads). If these change, the Unity ServiceClient must change too.

using System.Text.Json;
using Splatter.Protocol;
using Splatter.Service.Protocol;
using Xunit;

namespace Splatter.Service.Tests.Protocol;

public sealed class WireProtocolTests
{
    [Fact]
    public void Envelope_Serializes_CamelCase_With_String_Kind()
    {
        var envelope = MessageEnvelope.Request("conversation.create", new { workspaceId = "ws1" }, "ws1");

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);

        // kind must be a string, not an ordinal int.
        Assert.Contains("\"kind\":\"Request\"", json);
        Assert.Contains("\"protocol\":\"splatter.v1\"", json);
        Assert.Contains("\"type\":\"conversation.create\"", json);
        // camelCase property names.
        Assert.Contains("\"correlationId\":", json);
        Assert.DoesNotContain("\"Kind\":", json);
        Assert.DoesNotContain("\"Protocol\":", json);
    }

    [Fact]
    public void Envelope_Deserializes_CamelCase_String_Kind()
    {
        var json = """
        {
          "protocol": "splatter.v1",
          "kind": "Request",
          "id": "abc123",
          "correlationId": null,
          "workspaceId": "ws1",
          "sessionId": null,
          "type": "session.status",
          "timestamp": "2026-05-30T00:00:00+00:00",
          "payload": { "sessionId": "ses_1" }
        }
        """;

        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(envelope);
        Assert.Equal(MessageKind.Request, envelope!.Kind);
        Assert.Equal("session.status", envelope.Type);
        Assert.Equal("ws1", envelope.WorkspaceId);
        Assert.Equal("splatter.v1", envelope.Protocol);

        // Payload arrives as a JsonElement object (not a stringified blob).
        Assert.IsType<JsonElement>(envelope.Payload);
        var payload = (JsonElement)envelope.Payload!;
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("ses_1", payload.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void Envelope_Read_Is_CaseInsensitive()
    {
        // Tolerant of a client that sends PascalCase keys.
        var json = """
        { "Protocol": "splatter.v1", "Kind": "Event", "Id": "e1", "Type": "agent.message.delta",
          "Timestamp": "2026-05-30T00:00:00+00:00", "Payload": null }
        """;

        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);

        Assert.NotNull(envelope);
        Assert.Equal(MessageKind.Event, envelope!.Kind);
        Assert.Equal("agent.message.delta", envelope.Type);
    }

    [Fact]
    public void Payload_SnakeCase_Keys_Serialize_As_CamelCase()
    {
        // Service response payloads are written as anonymous objects with snake_case
        // keys (conversation_id, turn_id, session_id). They MUST go out as camelCase so
        // the Unity client, which expects camelCase, can read them back. Regression for
        // the "conversation_id is required" bug: the create response's id never parsed
        // on the client because it arrived as snake_case.
        var response = MessageEnvelope.Response("req-1", "conversation.created",
            new { conversation_id = "conv1", turn_id = "t1", session_id = "s1" }, "ws1");

        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);

        Assert.Contains("\"conversationId\":\"conv1\"", json);
        Assert.Contains("\"turnId\":\"t1\"", json);
        Assert.Contains("\"sessionId\":\"s1\"", json);
        Assert.DoesNotContain("conversation_id", json);
        Assert.DoesNotContain("turn_id", json);
        Assert.DoesNotContain("session_id", json);
    }

    [Fact]
    public void Response_Sets_CorrelationId_To_RequestId()
    {
        // The contract Unity must follow: match responses by correlationId, not id.
        var response = MessageEnvelope.Response("req-42", "conversation.created", new { id = "conv1" }, "ws1");

        Assert.Equal(MessageKind.Response, response.Kind);
        Assert.Equal("req-42", response.CorrelationId);
        Assert.NotEqual("req-42", response.Id); // id is a fresh message id
    }
}
