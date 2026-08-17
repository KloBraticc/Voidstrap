using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Voidstrap.Platform;

namespace Voidstrap.Desktop;

public sealed class NativeWebViewHost : IWebViewHost, IDisposable
{
	private readonly NativeWebView _webView;
	private readonly Func<Uri, CancellationToken, Task<OperationResult>>? _recreate;
	private bool _disposed;

	public NativeWebViewHost(NativeWebView webView, Func<Uri, CancellationToken, Task<OperationResult>>? recreate = null)
	{
		_webView = webView;
		_recreate = recreate;
		_webView.WebMessageReceived += OnWebMessageReceived;
	}

	public event EventHandler<BrowserMessageReceivedEventArgs>? MessageReceived;

	public Task<OperationResult> AddDocumentStartScriptAsync(string name, string script, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(OperationResult.Fail(
			"DocumentStartScriptRequiresNativeAdapter",
			"Document start scripts require a platform browser adapter"));
	}

	public async Task<OperationResult<string>> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? result = await _webView.InvokeScript(script);
			return OperationResult<string>.Success(result ?? string.Empty);
		}
		catch (OperationCanceledException)
		{
			return OperationResult<string>.Fail("OperationCanceled", "Script execution was canceled");
		}
		catch (Exception exception)
		{
			return OperationResult<string>.Fail("ScriptExecutionFailed", exception.Message);
		}
	}

	public Task<OperationResult> NavigateAsync(Uri uri, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			_webView.Source = uri;
			return Task.FromResult(OperationResult.Success());
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "Navigation was canceled"));
		}
		catch (Exception exception)
		{
			return Task.FromResult(OperationResult.Fail("NavigationFailed", exception.Message));
		}
	}

	public Task<OperationResult> RecreateAsync(Uri landingUri, CancellationToken cancellationToken = default)
	{
		if (_recreate is null)
		{
			return Task.FromResult(OperationResult.Fail(
				"BrowserRecreationRequiresHost",
				"Browser recreation requires the desktop host to replace the native control"));
		}

		return _recreate(landingUri, cancellationToken);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_webView.WebMessageReceived -= OnWebMessageReceived;
		_disposed = true;
		GC.SuppressFinalize(this);
	}

	private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
	{
		MessageReceived?.Invoke(this, new BrowserMessageReceivedEventArgs(e.Body ?? string.Empty));
	}
}
