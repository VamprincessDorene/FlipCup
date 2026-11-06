using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Protocol;

namespace FlipCup.Sync;

/// <summary>
/// Thin wrapper over an MQTT connection to a public relay (EMQX's free broker by default).
/// Incoming messages are queued; the game drains the queue on its own thread.
/// Reconnects automatically if the connection drops.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly IMqttClient client;
    private readonly CancellationTokenSource cts = new();
    private MqttClientOptions? options;
    private string[] topics = Array.Empty<string>();

    public ConcurrentQueue<(string Topic, string Payload)> Inbox { get; } = new();
    public bool Connected => client.IsConnected;
    public string Status { get; private set; } = "Not connected";

    public RelayClient()
    {
        client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            Inbox.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? ""));
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += async e =>
        {
            if (cts.IsCancellationRequested || options == null) return;
            Status = "Connection lost, reconnecting...";
            while (!cts.IsCancellationRequested && !client.IsConnected)
            {
                try
                {
                    await Task.Delay(3000, cts.Token);
                    await ConnectAndSubscribe();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Status = "Reconnecting... (" + ex.Message + ")"; }
            }
        };
    }

    public async Task ConnectAsync(string host, int port, string clientId, params string[] subscribeTo)
    {
        topics = subscribeTo;
        options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithTlsOptions(o => o.UseTls())
            .WithClientId(clientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
            .Build();
        Status = $"Connecting to {host}...";
        await ConnectAndSubscribe();
    }

    private async Task ConnectAndSubscribe()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(options!, timeout.Token);
        foreach (var t in topics)
        {
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(t).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(), timeout.Token);
        }
        Status = "Connected";
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        if (!client.IsConnected) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        try { await client.PublishAsync(msg, cts.Token); }
        catch (Exception ex) { Status = "Send failed: " + ex.Message; }
    }

    /// <summary>Publish an empty retained message: removes a stale game from the relay.</summary>
    public Task ClearRetainedAsync(string topic) => PublishAsync(topic, "", retain: true);

    public async Task DisconnectAsync()
    {
        cts.Cancel();
        if (client.IsConnected)
        {
            try { await client.DisconnectAsync(); } catch { /* best effort */ }
        }
        Status = "Not connected";
    }

    public void Dispose()
    {
        if (!cts.IsCancellationRequested) cts.Cancel();
        client.Dispose();
        cts.Dispose();
    }
}
