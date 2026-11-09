using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Rhino;
using Rhino.PlugIns;

namespace RhinoMagpie.Plugin;

internal static class DocumentMcp
{
    public static Uri EnsureConnected(RhinoDoc doc)
    {
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber)
            throw new InvalidOperationException("活动文档已切换，请重新发送。");
        PlugIn.LoadPlugIn(new Guid("2668d7ed-f507-4a68-8295-8172147a0e39"));
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => item.GetName().Name == "RhinoAI");
        var method = assembly?.GetType("Rhino.AI.RhinoAIHost")
            ?.GetMethod("TryGetPortFor", BindingFlags.Public | BindingFlags.Static);
        object?[] arguments = [doc, 0];
        try
        {
            if (method?.Invoke(null, arguments) is true && arguments[1] is int existingPort)
                return Endpoint(existingPort);
        }
        catch (Exception error) when (error is TargetInvocationException or ArgumentException)
        {
            RhinoApp.WriteLine("Miki-Rhino 将通过 MCPStart 连接当前文档。");
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        if (!RhinoApp.RunScript($"_MCPStart {port} _Enter", false))
            throw new InvalidOperationException("无法启动 Rhino MCP。请确认 Rhino 9 的 RhinoAI 插件已启用。");
        return Endpoint(port);
    }

    private static Uri Endpoint(int port) => new($"http://localhost:{port}/agent");
}
