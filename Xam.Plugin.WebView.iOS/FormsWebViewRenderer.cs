﻿using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Foundation;
using WebKit;
using Xam.Plugin.WebView.Abstractions;
using Xam.Plugin.WebView.Abstractions.Enumerations;
using Xam.Plugin.WebView.iOS;
using Xamarin.Forms.Platform.iOS;
using UIKit;
using System.Net;

[assembly: Xamarin.Forms.ExportRenderer(typeof(FormsWebView), typeof(FormsWebViewRenderer))]
namespace Xam.Plugin.WebView.iOS
{
    public class FormsWebViewRenderer : ViewRenderer<FormsWebView, WKWebView>, IWKScriptMessageHandler, IWKUIDelegate
    {

        public static event EventHandler<WKWebView> OnControlChanged;

        public static string BaseUrl { get; set; } = NSBundle.MainBundle.BundlePath;

        FormsNavigationDelegate _navigationDelegate;

        WKWebViewConfiguration _configuration;

        WKUserContentController _contentController;

        public static void Initialize() {
            var dt = DateTime.Now;
        }

        protected override void OnElementChanged(ElementChangedEventArgs<FormsWebView> e)
        {
            base.OnElementChanged(e);

            if (Control == null && Element != null)
                SetupControl();

            if (e.NewElement != null)
                SetupElement(e.NewElement);

            if (e.OldElement != null)
                DestroyElement(e.OldElement);
        }

		void SetupElement(FormsWebView element)
		{
            NSHttpCookieStorage.SharedStorage.AcceptPolicy = NSHttpCookieAcceptPolicy.Always;
            element.PropertyChanged += OnPropertyChanged;
            element.OnJavascriptInjectionRequest += OnJavascriptInjectionRequest;
            element.OnClearCookiesRequested += OnClearCookiesRequest;
            element.OnGetAllCookiesRequestedAsync += OnGetAllCookiesRequestAsync;
            element.OnGetCookieRequestedAsync += OnGetCookieRequestAsync;
            element.OnSetCookieRequestedAsync += OnSetCookieRequestAsync;
            element.OnBackRequested += OnBackRequested;
            element.OnForwardRequested += OnForwardRequested;
            element.OnRefreshRequested += OnRefreshRequested;

            SetSource();
		}

        void DestroyElement(FormsWebView element)
        {
            element.PropertyChanged -= OnPropertyChanged;
            element.OnJavascriptInjectionRequest -= OnJavascriptInjectionRequest;
            element.OnClearCookiesRequested -= OnClearCookiesRequest;
            element.OnGetAllCookiesRequestedAsync += OnGetAllCookiesRequestAsync;
            element.OnGetCookieRequestedAsync += OnGetCookieRequestAsync;
            element.OnSetCookieRequestedAsync += OnSetCookieRequestAsync;
            element.OnBackRequested -= OnBackRequested;
            element.OnForwardRequested -= OnForwardRequested;
            element.OnRefreshRequested -= OnRefreshRequested;

            element.Dispose();
        }

        void SetupControl()
        {
            _navigationDelegate = new FormsNavigationDelegate(this);
            _contentController = new WKUserContentController();
            _contentController.AddScriptMessageHandler(this, "invokeAction");
            _configuration = new WKWebViewConfiguration {
                UserContentController = _contentController
            };

            var wkWebView = new WKWebView(Frame, _configuration)
            {
                Opaque = false,
                UIDelegate = this,
                NavigationDelegate = _navigationDelegate
            };

            FormsWebView.CallbackAdded += OnCallbackAdded;

            SetNativeControl(wkWebView);
            OnControlChanged?.Invoke(this, wkWebView);
        }

        async void OnCallbackAdded(object sender, string e)
        {
            if (Element == null || string.IsNullOrWhiteSpace(e)) return;

            if ((sender == null && Element.EnableGlobalCallbacks) || sender != null)
                await OnJavascriptInjectionRequest(FormsWebView.GenerateFunctionScript(e));
        }

        void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
            switch (e.PropertyName) {
                case "Source":
                    SetSource();
                    break;
            }
		}

        private async Task OnClearCookiesRequest()
        {
            if (Control == null) return;

            var store = _configuration.WebsiteDataStore.HttpCookieStore;

            var cookies = await store.GetAllCookiesAsync();
            foreach (var c in cookies) {
                await store.DeleteCookieAsync(c);
            }

            /* Delete shared-storage for url also */

            var url = new Uri(Element.Source);
            NSHttpCookie[] sharedCookies = NSHttpCookieStorage.SharedStorage.CookiesForUrl(url);
            foreach(NSHttpCookie c in sharedCookies) {
                NSHttpCookieStorage.SharedStorage.DeleteCookie(c);
            }

        }

        /* 
          * Sets cookievalue based on cookiename. 
          * If the cookie has specified an expirationdate now or earlier than 
          * now, the cookie will be deleted. To create a sessioncookie don't
          * specify any expirationdate
         */

        private async Task<string> OnSetCookieRequestAsync(Cookie cookie)
        {
            if (Control == null) return string.Empty;
            var toReturn = string.Empty;
            try
            {
                var url = new Uri(Element.Source);
                var domain = url.Host;
                var newCookie = new NSHttpCookie(cookie);

                // Adding cookie to Shared cookie storage, and WebsiteDataStore

                NSHttpCookieStorage.SharedStorage.SetCookie(newCookie);

                var store = _configuration.WebsiteDataStore.HttpCookieStore;
                store.SetCookie(newCookie, () => {});

                toReturn = await OnGetCookieRequestAsync(cookie.Name);

            }
            catch (Exception e)
            {
                Console.WriteLine("We had a crash " + e);
                toReturn = string.Empty;
            }
            return toReturn;
        }

        /* gets all cookies on current page */

        private async Task<string> OnGetAllCookiesRequestAsync() {
            if (Control == null || Element == null)
            {
                return string.Empty;
            }
            var cookieCollection = string.Empty;
            var url = new Uri(Element.Source);

            NSHttpCookie[] sharedCookies = NSHttpCookieStorage.SharedStorage.CookiesForUrl(url);
            foreach (NSHttpCookie c in sharedCookies)
            {
                if (c.Domain == url.Host)
                {
                    cookieCollection += c.Name + "=" + c.Value + "; ";
                }
            }

            var store = _configuration.WebsiteDataStore.HttpCookieStore;

            var cookies = await store.GetAllCookiesAsync();
            foreach (var c in cookies)
            {
                if (c.Domain == url.Host)
                {
                    cookieCollection += c.Name + "=" + c.Value + "; ";
                }
            }
            if(cookieCollection.Length > 0) {
                cookieCollection = cookieCollection.Remove(cookieCollection.Length - 2);
            }
            return cookieCollection;
        }

        /* gets cookie based on cookiekey. 
         * Fetches from WebsiteDataStore first, and if doesn't find the cookie 
         * there, it searches through the sharedstorage on the current domain.
         */

        private async Task<string> OnGetCookieRequestAsync(string key)
        {
            if (Control == null || Element == null) return string.Empty;
            var url = new Uri(Element.Source);
            var toReturn = string.Empty;

            var store = _configuration.WebsiteDataStore.HttpCookieStore;

            var cookies = await store.GetAllCookiesAsync();
            foreach (var c in cookies)
            {
                if (c.Name == key && c.Domain == url.Host)
                    return c.Value;
            }

            NSHttpCookie[] sharedCookies = NSHttpCookieStorage.SharedStorage.CookiesForUrl(url);
            foreach (NSHttpCookie c in sharedCookies)
            {
                if (c.Name == key && c.Domain == url.Host)
                {
                    return c.Value;
                }
            }

            return string.Empty;
        }

        internal async Task<string> OnJavascriptInjectionRequest(string js)
		{
            if (Control == null || Element == null) return string.Empty;

            var response = string.Empty;

            try
            {
                var obj = await Control.EvaluateJavaScriptAsync(js).ConfigureAwait(true);
                if (obj != null)
                    response = obj.ToString();
            }

            catch (Exception) { /* The Webview might not be ready... */ }
            return response;
		}

        void SetSource()
        {
            if (Element == null || Control == null || string.IsNullOrWhiteSpace(Element.Source)) return;

            switch (Element.ContentType) {
                case WebViewContentType.Internet:
                    LoadInternetContent();
                    break;

                case WebViewContentType.LocalFile:
                    LoadLocalFile();
                    break;

                case WebViewContentType.StringData:
                    LoadStringData();
                    break;
            }
        }

        void LoadStringData()
        {
            if (Control == null || Element == null) return;

            var nsBaseUri = new NSUrl($"file://{Element.BaseUrl ?? BaseUrl}");
            Control.LoadHtmlString(Element.Source, nsBaseUri);
        }

        void LoadLocalFile()
        {
            if (Control == null || Element == null) return;

            var path = Path.Combine(Element.BaseUrl ?? BaseUrl, Element.Source);
            var nsFileUri = new NSUrl($"file://{path}");
            var nsBaseUri = new NSUrl($"file://{Element.BaseUrl ?? BaseUrl}");

            Control.LoadFileUrl(nsFileUri, nsBaseUri);
        }

        void LoadInternetContent()
        {
            if (Control == null || Element == null) return;

            var headers = new NSMutableDictionary();

            foreach (var header in Element.LocalRegisteredHeaders)
            {
                var key = new NSString(header.Key);
                if (!headers.ContainsKey(key))
                    headers.Add(key, new NSString(header.Value));
            }

            if (Element.EnableGlobalHeaders)
            {
                foreach (var header in FormsWebView.GlobalRegisteredHeaders)
                {
                    var key = new NSString(header.Key);
                    if (!headers.ContainsKey(key))
                        headers.Add(key, new NSString(header.Value));
                }
            }

            var url = new NSUrl(Element.Source);
            var request = new NSMutableUrlRequest(url)
            {
                Headers = headers
            };

            Control.LoadRequest(request);
        }

        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (Element == null || message == null || message.Body == null) return;
            Element.HandleScriptReceived(message.Body.ToString());
        }

        void OnRefreshRequested(object sender, EventArgs e)
        {
            if (Control == null) return;
            Control.ReloadFromOrigin();
        }

        void OnForwardRequested(object sender, EventArgs e)
        {
            if (Control == null || Element == null) return;

            if (Control.CanGoForward)
                Control.GoForward();
        }

        void OnBackRequested(object sender, EventArgs e)
        {
            if (Control == null || Element == null) return;

            if (Control.CanGoBack)
                Control.GoBack();
        }

        /**
         * UI Delegate methods from: https://developer.xamarin.com/recipes/ios/content_controls/web_view/handle_javascript_alerts/
         */

        [Export("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:")]
        public void RunJavaScriptAlertPanel(WebKit.WKWebView webView, string message, WKFrameInfo frame, Action completionHandler)
        {
            var alertController = UIAlertController.Create(null, message, UIAlertControllerStyle.Alert);
            alertController.AddAction(UIAlertAction.Create("Ok", UIAlertActionStyle.Default, null));
            UIApplication.SharedApplication.KeyWindow.RootViewController.PresentViewController(alertController, true, null);
            
            completionHandler();
        }



        [Export("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:")]
        public void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completionHandler)
        {
            var alertController = UIAlertController.Create(null, message, UIAlertControllerStyle.Alert);
            
            alertController.AddAction(UIAlertAction.Create("Ok", UIAlertActionStyle.Default, okAction => {

                completionHandler(true);

            }));
            
            alertController.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Default, cancelAction => {

                completionHandler(false);

            }));
            
            UIApplication.SharedApplication.KeyWindow.RootViewController.PresentViewController(alertController, true, null);
        }



        [Export("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:")]
        public void RunJavaScriptTextInputPanel(WKWebView webView, string prompt, string defaultText, WebKit.WKFrameInfo frame, System.Action<string> completionHandler)
        {
            var alertController = UIAlertController.Create(null, prompt, UIAlertControllerStyle.Alert);
            
            UITextField alertTextField = null;
            alertController.AddTextField(textField => {
                textField.Placeholder = defaultText;
                alertTextField = textField;
            });
            
            alertController.AddAction(UIAlertAction.Create("Ok", UIAlertActionStyle.Default, okAction => {

                completionHandler(alertTextField.Text);

            }));
            
            alertController.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Default, cancelAction => {

                completionHandler(null);

            }));

            UIApplication.SharedApplication.KeyWindow.RootViewController.PresentViewController(alertController, true, null);
        }

    }
}
