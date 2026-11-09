using System.Diagnostics;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace RhinoMagpie.Plugin;

internal sealed class NativeAiIntegration : IDisposable
{
    private static readonly Guid NativePanelId = new("fb948c98-5987-45a3-8dcb-2814ed77ee3b");
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<Panel, Host> hosts = [];
    private Host? activeHost;
    private uint pendingDocument;
    private long lastCheck;
    private bool disposed;
    private bool available = true;

    public NativeAiIntegration()
    {
        RhinoApp.Idle += OnIdle;
        RhinoDoc.CloseDocument += OnDocumentClosed;
    }

    public bool OpenMagpie()
    {
        if (disposed || !available) return false;
        try
        {
            if (!PlugIn.LoadPlugIn(new Guid("2668d7ed-f507-4a68-8295-8172147a0e39"))) return false;
            Panels.OpenPanel(NativePanelId);
            AttachIfAvailable();
            if (activeHost is null || RhinoDoc.ActiveDoc is not { } doc) return false;
            pendingDocument = doc.RuntimeSerialNumber;
            return true;
        }
        catch (Exception error)
        {
            Disable(error);
            return false;
        }
    }

    private void OnIdle(object? sender, EventArgs args)
    {
        if (disposed || !available || clock.ElapsedMilliseconds - lastCheck < 500) return;
        lastCheck = clock.ElapsedMilliseconds;
        try
        {
            AttachIfAvailable();
            foreach (var entry in hosts.ToList())
            {
                if (entry.Key.IsDisposed)
                {
                    entry.Value.Dispose();
                    hosts.Remove(entry.Key);
                }
                else entry.Value.UpdateAvailability();
            }
            if (pendingDocument != 0)
            {
                if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != pendingDocument) pendingDocument = 0;
                else if (activeHost?.SelectMagpie() == true) pendingDocument = 0;
            }
        }
        catch (Exception error) { Disable(error); }
    }

    private void Disable(Exception error)
    {
        available = false;
        foreach (var host in hosts.Values) host.Dispose();
        hosts.Clear();
        activeHost = null;
        pendingDocument = 0;
        RhinoApp.WriteLine("Miki-Rhino 的原生面板兼容入口不可用，可使用 MikiRhinoStandalone。原因：" + error.GetType().Name);
    }

    private void AttachIfAvailable()
    {
        var doc = RhinoDoc.ActiveDoc;
        activeHost = null;
        if (doc is null) return;
        var panel = Panels.GetPanel(NativePanelId, doc) as Panel;
        if (panel is null || panel.IsDisposed || panel.GetType().FullName != "Rhino.AI.UI.AIPanel") return;
        if (hosts.TryGetValue(panel, out var existing))
        {
            activeHost = existing;
            return;
        }
        if (panel.Content is not WebView original) return;
        activeHost = new Host(panel, original, doc.RuntimeSerialNumber);
        hosts.Add(panel, activeHost);
    }

    private void OnDocumentClosed(object? sender, DocumentEventArgs args)
    {
        foreach (var entry in hosts.Where(entry => entry.Value.DocumentSerial == args.DocumentSerialNumber).ToList())
        {
            entry.Value.Dispose();
            hosts.Remove(entry.Key);
            if (activeHost == entry.Value) activeHost = null;
        }
        if (pendingDocument == args.DocumentSerialNumber) pendingDocument = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RhinoApp.Idle -= OnIdle;
        RhinoDoc.CloseDocument -= OnDocumentClosed;
        foreach (var host in hosts.Values) host.Dispose();
        hosts.Clear();
        activeHost = null;
    }

    private sealed class Host : IDisposable
    {
        private const string EntryScript = """
            (() => {
                const key = '__rhinoMikiNativeEntry';
                const url = __ENTRY_URL__;
                const previous = window[key];
                if (previous && previous.url === url) {
                    previous.refresh(__ENABLED__);
                    return;
                }
                if (previous) previous.dispose();
                let enabled = __ENABLED__;
                const owned = new Set();
                const busy = () => !!document.querySelector('.send.stop, .agent-chip .dot.busy');
                const create = (menu) => {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.dataset.rhinoMikiEntry = menu ? 'menu' : 'header';
                    button.className = menu ? 'menu-item' : 'icon-btn';
                    button.title = 'Miki-Rhino';
                    button.setAttribute('aria-label', 'Miki-Rhino');
                    if (menu) {
                        const dot = document.createElement('span');
                        dot.className = 'dot ready';
                        const body = document.createElement('span');
                        body.className = 'body';
                        const label = document.createElement('b');
                        label.textContent = 'Miki-Rhino';
                        const detail = document.createElement('span');
                        detail.textContent = 'OpenAI-compatible';
                        body.append(label, detail);
                        button.append(dot, body);
                    } else {
                        button.textContent = 'Miki-Rhino';
                    }
                    button.addEventListener('click', (event) => {
                        event.preventDefault();
                        event.stopPropagation();
                        if (enabled && !busy()) window.open(url, '_blank');
                    });
                    owned.add(button);
                    return button;
                };
                const refresh = (value = enabled) => {
                    enabled = value;
                    for (const button of owned) {
                        if (!button.isConnected) owned.delete(button);
                    }
                    let knownMenu = false;
                    for (const menu of document.querySelectorAll('.popover[role="menu"]')) {
                        const separator = menu.querySelector(':scope > .menu-sep');
                        if (!separator) continue;
                        knownMenu = true;
                        if (!menu.querySelector('[data-rhino-miki-entry="menu"]')) {
                            menu.insertBefore(create(true), separator);
                        }
                    }
                    const header = document.querySelector('header.header');
                    if (header) {
                        const chip = header.querySelector('.agent-chip');
                        const fallback = !chip || (chip.getAttribute('aria-expanded') === 'true' && !knownMenu);
                        const button = header.querySelector('[data-rhino-miki-entry="header"]');
                        if (fallback && !button) header.append(create(false));
                        if (!fallback && button) {
                            owned.delete(button);
                            button.remove();
                        }
                    }
                    const disabled = !enabled || busy();
                    for (const button of owned) {
                        if (button.disabled !== disabled) button.disabled = disabled;
                    }
                };
                const observer = new MutationObserver(() => refresh());
                window[key] = {
                    url,
                    refresh,
                    dispose() {
                        observer.disconnect();
                        for (const button of owned) button.remove();
                        owned.clear();
                        delete window[key];
                    }
                };
                observer.observe(document.documentElement, {
                    childList: true, subtree: true, attributes: true,
                    attributeFilter: ['class', 'disabled']
                });
                refresh();
            })();
            """;

        public Panel NativePanel { get; }
        public uint DocumentSerial { get; }
        private readonly WebView original;
        private readonly Uri entryUri = new("rhino-miki://native-entry/" + Guid.NewGuid().ToString("N"));
        private readonly Button returnButton = new() { Text = "返回 Rhino 原生代理" };
        private readonly DynamicLayout wrapper = new() { Spacing = new Size(0, 0) };
        private MagpiePanel? magpie;
        private bool showingMagpie;
        private bool switching;
        private bool injecting;
        private bool disposed;

        public Host(Panel panel, WebView original, uint documentSerial)
        {
            NativePanel = panel;
            DocumentSerial = documentSerial;
            this.original = original;
            var toolbar = new DynamicLayout { Padding = new Padding(8, 6), Spacing = new Size(8, 0) };
            toolbar.AddSeparateRow(new Label { Text = "Miki-Rhino", VerticalAlignment = VerticalAlignment.Center }, returnButton);
            wrapper.AddRow(toolbar);
            returnButton.Click += OnReturn;
            original.OpenNewWindow += OnOpenNewWindow;
            original.DocumentLoaded += OnDocumentLoaded;
        }

        private bool CanSwitch => !disposed && !NativePanel.IsDisposed &&
            RhinoDoc.ActiveDoc?.RuntimeSerialNumber == DocumentSerial &&
            magpie?.IsBusy != true && RhinoDoc.ActiveDoc?.UndoRecordingIsActive != true &&
            NativePanel.Content == (showingMagpie ? (Control)wrapper : original);

        public bool SelectMagpie()
        {
            if (!CanSwitch) return false;
            if (showingMagpie) return true;
            if (switching) return false;
            _ = SelectMagpieAsync();
            return true;
        }

        public void UpdateAvailability()
        {
            if (disposed || NativePanel.IsDisposed) return;
            returnButton.Enabled = CanSwitch && !switching;
            if (!showingMagpie) _ = InjectAsync();
        }

        private void OnDocumentLoaded(object? sender, WebViewLoadedEventArgs args) => _ = InjectAsync();

        private void OnOpenNewWindow(object? sender, WebViewNewWindowEventArgs args)
        {
            if (args.Uri != entryUri) return;
            args.Cancel = true;
            if (CanSwitch)
                Application.Instance.AsyncInvoke(() => SelectMagpie());
        }

        private async Task InjectAsync()
        {
            if (disposed || original.IsDisposed || injecting) return;
            injecting = true;
            try
            {
                await original.ExecuteScriptAsync(EntryScript
                    .Replace("__ENTRY_URL__", JsonSerializer.Serialize(entryUri.AbsoluteUri))
                    .Replace("__ENABLED__", CanSwitch ? "true" : "false"));
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException
                or System.Runtime.InteropServices.COMException)
            {
                Debug.WriteLine("Miki-Rhino native entry injection: " + error.GetType().Name);
            }
            finally { injecting = false; }
        }

        private async Task SelectMagpieAsync()
        {
            switching = true;
            try
            {
                string ready = await original.ExecuteScriptAsync(
                    "return document.querySelector('.send.stop, .agent-chip .dot.busy') ? 'busy' : 'ready';");
                if (ready != "ready" || !CanSwitch || NativePanel.Content != original) return;
                if (magpie is null)
                {
                    magpie = new MagpiePanel();
                    wrapper.Add(magpie, xscale: true, yscale: true);
                }
                NativePanel.Content = null;
                try { NativePanel.Content = wrapper; }
                catch
                {
                    NativePanel.Content = original;
                    throw;
                }
                showingMagpie = true;
                returnButton.Enabled = CanSwitch;
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine("Miki-Rhino 面板未能打开：" + error.GetType().Name);
            }
            finally { switching = false; }
        }

        private void OnReturn(object? sender, EventArgs args)
        {
            if (!CanSwitch || switching || !showingMagpie) return;
            NativePanel.Content = null;
            try { NativePanel.Content = original; }
            catch (Exception error)
            {
                NativePanel.Content = wrapper;
                RhinoApp.WriteLine("Rhino 原生代理未能恢复：" + error.GetType().Name);
                return;
            }
            showingMagpie = false;
            _ = InjectAsync();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            original.OpenNewWindow -= OnOpenNewWindow;
            original.DocumentLoaded -= OnDocumentLoaded;
            returnButton.Click -= OnReturn;
            if (!original.IsDisposed) _ = RemoveEntryAsync();
            if (!NativePanel.IsDisposed && NativePanel.Content == wrapper && !original.IsDisposed)
            {
                NativePanel.Content = null;
                NativePanel.Content = original;
            }
            else if (NativePanel.IsDisposed && !original.IsDisposed)
                original.Dispose();
            if (magpie?.IsDisposed == false) magpie.Dispose();
            if (!wrapper.IsDisposed) wrapper.Dispose();
        }

        private async Task RemoveEntryAsync()
        {
            try
            {
                await original.ExecuteScriptAsync(
                    "const entry = window.__rhinoMikiNativeEntry; if (entry && entry.url === " +
                    JsonSerializer.Serialize(entryUri.AbsoluteUri) + ") entry.dispose();");
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException
                or System.Runtime.InteropServices.COMException)
            {
                Debug.WriteLine("Miki-Rhino native entry cleanup: " + error.GetType().Name);
            }
        }
    }
}
