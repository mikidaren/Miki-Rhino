using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eto.Forms;

namespace RhinoMagpie.Plugin;

internal sealed class PanelWebView : WebView
{
    private string? pendingState;
    private bool ready;
    private bool sending;
    private string? lastSent;

    public event Action<JsonObject>? ActionReceived;

    public PanelWebView()
    {
        BrowserContextMenuEnabled = true;
        MessageReceived += (_, args) =>
        {
            try
            {
                if (args.Message.Length <= 1_000_000 && JsonNode.Parse(args.Message) is JsonObject message)
                    ActionReceived?.Invoke(message);
            }
            catch (JsonException) { }
        };
        DocumentLoading += (_, args) =>
        {
            if (args.IsMainFrame && args.Uri.Scheme is not ("about" or "data" or "file"))
                args.Cancel = true;
        };
        DocumentLoaded += (_, _) =>
        {
            ready = true;
            lastSent = null;
            _ = SendPendingStateAsync();
        };
        using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MikiRhino.Plugin.Web.panel.html")
            ?? throw new InvalidOperationException("Miki-Rhino 面板资源缺失，请重新安装插件。");
        using var reader = new StreamReader(resource);
        LoadHtml(reader.ReadToEnd());
    }

    public void UpdateState(object state)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        pendingState = json;
        if (ready && !IsDisposed && lastSent != pendingState)
            _ = SendPendingStateAsync();
    }

    private async Task SendPendingStateAsync()
    {
        if (sending || !ready || IsDisposed) return;
        sending = true;
        try
        {
            while (!IsDisposed && pendingState is not null && pendingState != lastSent)
            {
                string state = pendingState;
                string result = await ExecuteScriptAsync(
                    "if(window.magpie){window.magpie.applyState(" + state + ");return 'ok';}return 'waiting';");
                if (result != "ok") return;
                lastSent = state;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            ready = false;
        }
        finally { sending = false; }
    }
}
