using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NovaBrowser;

public partial class MainWindow : Window
{
    private const string StartUrl = "https://www.google.com/";
    private readonly List<BrowserTab> _tabs = [];
    private readonly string _dataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaBrowser", "WebView2");
    private CoreWebView2Environment? _environment;
    private BrowserTab? _active;

    // Small starter list only. This is NOT a comprehensive tracking-protection engine.
    private static readonly string[] BlockedHosts =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "scorecardresearch.com", "adsrvr.org", "taboola.com", "outbrain.com"
    ];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_dataPath);
                _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _dataPath);
                await OpenTabAsync(StartUrl);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Falha ao iniciar o motor do navegador";
                MessageBox.Show("Não foi possível iniciar WebView2:\n" + ex.Message +
                    "\n\nInstale ou atualize o WebView2 Runtime.", "Nova Browser",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private sealed class BrowserTab
    {
        public required WebView2 View { get; init; }
        public required Button Header { get; init; }
        public required StackPanel Container { get; init; }
        public string Title { get; set; } = "Nova aba";
    }

    private async Task OpenTabAsync(string url)
    {
        if (_environment is null) return;
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        var header = new Button
        {
            Content = "Nova aba",
            MinWidth = 110,
            MaxWidth = 210,
            Padding = new Thickness(15, 7, 12, 7),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        var close = new Button
        {
            Content = "×", Width = 26, Height = 29,
            Background = Brushes.Transparent, Foreground = Brushes.LightGray,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        var container = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 5, 2, 3) };
        container.Children.Add(header);
        container.Children.Add(close);
        var tab = new BrowserTab { View = view, Header = header, Container = container };
        header.Click += (_, _) => Activate(tab);
        close.Click += (_, _) => CloseTab(tab);

        _tabs.Add(tab);
        TabStrip.Children.Add(container);
        BrowserHost.Children.Add(view);
        Activate(tab);

        try
        {
            await view.EnsureCoreWebView2Async(_environment);
            var core = view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false; // Preview default; configurable later.

            core.PermissionRequested += (_, args) =>
            {
                // Fail closed for sensitive permissions until a proper per-site permission UI exists.
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            };
            core.NewWindowRequested += (_, args) =>
            {
                // Block unsolicited popups in preview. No popup/tab delegation implemented yet.
                args.Handled = true;
                StatusText.Text = "Popup bloqueado";
            };
            core.NavigationStarting += (_, args) =>
            {
                if (!IsAllowedNavigation(args.Uri))
                {
                    args.Cancel = true;
                    StatusText.Text = "Navegação bloqueada: endereço não permitido";
                }
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)) return;
                if (!IsBlockedHost(uri.Host)) return;
                args.Response = core.Environment.CreateWebResourceResponse(
                    null, 403, "Blocked by Nova Shield", "Content-Type: text/plain");
                StatusText.Text = "Nova Shield bloqueou uma solicitação";
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Nova aba" : core.DocumentTitle;
                tab.Header.Content = tab.Title.Length > 21 ? tab.Title[..20] + "…" : tab.Title;
                if (_active == tab) Title = tab.Title + " — Nova Browser";
            };
            core.SourceChanged += (_, _) =>
            {
                if (_active == tab) UpdateAddress();
            };
            core.HistoryChanged += (_, _) =>
            {
                if (_active == tab) UpdateButtons();
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (_active == tab) StatusText.Text = args.IsSuccess ? "Pronto" : "Falha ao carregar a página";
            };
            if (_tabs.Contains(tab)) core.Navigate(url);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Falha ao abrir a aba: " + ex.Message;
        }
    }

    private static bool IsBlockedHost(string host) =>
        BlockedHosts.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedNavigation(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ||
         uri.Scheme == "about");

    private void Activate(BrowserTab tab)
    {
        _active = tab;
        foreach (var item in _tabs)
        {
            item.View.Visibility = item == tab ? Visibility.Visible : Visibility.Collapsed;
            item.Container.Background = item == tab
                ? new SolidColorBrush(Color.FromRgb(39, 52, 73))
                : Brushes.Transparent;
            item.Header.Foreground = item == tab ? Brushes.White : Brushes.LightGray;
        }
        Title = tab.Title + " — Nova Browser";
        UpdateAddress();
        UpdateButtons();
    }

    private void UpdateAddress()
    {
        var address = _active?.View.Source?.AbsoluteUri ?? "";
        AddressBox.Text = address == "about:blank" ? "" : address;
        SecurityIndicator.Text = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "♢" : "!";
        SecurityIndicator.Foreground = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.FromRgb(96, 225, 194))
            : Brushes.Orange;
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = _active?.View.CoreWebView2?.CanGoBack == true;
        ForwardButton.IsEnabled = _active?.View.CoreWebView2?.CanGoForward == true;
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Container);
        BrowserHost.Children.Remove(tab.View);
        tab.View.Dispose();
        if (_active == tab)
        {
            _active = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
            else _ = OpenTabAsync(StartUrl);
        }
    }

    private void NavigateFromAddress()
    {
        var input = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        string url;
        if (Uri.TryCreate(input, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == "https" || parsed.Scheme == "http"))
            url = parsed.AbsoluteUri;
        else if (!input.Contains(' ') && input.Contains('.') &&
                 !input.Contains('/') && !input.Contains(':'))
            url = "https://" + input;
        else
            url = "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
        _active?.View.CoreWebView2?.Navigate(url);
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await OpenTabAsync(StartUrl);
    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NavigateFromAddress(); Keyboard.ClearFocus(); }
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2?.CanGoBack == true) _active.View.GoBack();
    }
    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2?.CanGoForward == true) _active.View.GoForward();
    }
    private void Reload_Click(object sender, RoutedEventArgs e) => _active?.View.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => _active?.View.CoreWebView2?.Navigate(StartUrl);
    private void Shield_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show("Nova Shield Preview\n\n• Bloqueio de alguns domínios de anúncios\n" +
            "• Permissões de sites negadas por padrão\n• Popups bloqueados\n" +
            "• Navegação restrita a HTTP/HTTPS/about\n\n" +
            "Ainda NÃO é um bloqueador completo de anúncios nem proteção antiphishing.",
            "Nova Shield", MessageBoxButton.OK, MessageBoxImage.Information);

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            e.Handled = true;
            await OpenTabAsync(StartUrl);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && _active is not null)
        {
            e.Handled = true;
            CloseTab(_active);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            e.Handled = true;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
    }
}
