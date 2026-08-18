using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.DependencyInjection;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

public class Program
{
	private static void FixUrl(RewriteContext context)
	{
		HttpRequest request = context.HttpContext.Request;
		if (request.Path.Value != null && request.Path.Value.StartsWith("//"))
		{
			string value = request.Path.Value;
			request.Path = new PathString(value.Substring(1, value.Length - 1));
		}
	}

	private static string[] DefaultPrefixes()
	{
		return new string[1] { "http://+:80/" };
	}

	private static async Task OnlyAllowLocal(HttpContext context, RequestDelegate next)
	{
		if (context.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
		{
			context.Response.StatusCode = 401;
		}
		else
		{
			await next(context);
		}
	}

	private static readonly object _unhandledLogLock = new object();
	private static long _unhandledWindowStarted = Environment.TickCount64;
	private static int _unhandledInWindow;

	private static bool ShouldLogUnhandled()
	{
		lock (_unhandledLogLock)
		{
			long now = Environment.TickCount64;
			if (now - _unhandledWindowStarted >= 60000)
			{
				_unhandledWindowStarted = now;
				_unhandledInWindow = 0;
			}
			if (_unhandledInWindow >= 20)
				return false;
			_unhandledInWindow++;
			return true;
		}
	}

	private static async Task CaptureUnhandled(HttpContext context, RequestDelegate next)
	{
		await next(context);
		if (context.Response.StatusCode != 404)
		{
			return;
		}
		if (!ShouldLogUnhandled())
			return;
		HttpRequest request = context.Request;
		string path = request.Path.Value ?? "";
		if (path.Length > 512)
			path = path.Substring(0, 512);
		Logger.Instance.Warn("Unhandled endpoint: " + request.Method + " " + path);
	}

	private static void WatchProcessId(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			process.WaitForExit();
		}
		catch (Exception)
		{
		}
		Environment.Exit(123);
	}

	public static void Main(string[] args)
	{
		AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler.Handle;
		string text = null;
		int processId = 0;
		bool isRenderMode = false;
		bool useHttpSys = false;
		string httpSysPrefixes = null;
		foreach (string text2 in args)
		{
			if (text2.StartsWith("--prefixes="))
			{
				httpSysPrefixes = text2.Substring(11);
				useHttpSys = true;
				continue;
			}
			if (text2.StartsWith("--client="))
			{
				string text3 = text2;
				text = text3.Substring(9, text3.Length - 9).Trim();
			}
			else if (text2.StartsWith("--pid="))
			{
				string text3 = text2;
				string text4 = text3.Substring(6, text3.Length - 6).Trim();
				if (!int.TryParse(text4, out processId))
				{
					Logger.Instance.Warn("Invalid process ID provided: " + text4);
				}
			}
			else if (text2 == "--rm")
			{
				isRenderMode = true;
			}
			else if (text2 == "--httpsys")
			{
				useHttpSys = true;
			}
		}
		if (text == null)
		{
			Logger.Instance.Error("Failed to initialise config");
			AppDomain.CurrentDomain.UnhandledException -= UnhandledExceptionHandler.Handle;
			return;
		}
		Logger.Instance.Info("Client: " + text);
		Config.Init(text);
		Config.Instance.IsRenderMode = isRenderMode;
		_ = Common.AssetPackDirectories;
		_ = LocalAssetHelper.Instance;
		WebApplicationBuilder webApplicationBuilder = WebApplication.CreateBuilder(args);
		if (useHttpSys)
		{
			webApplicationBuilder.WebHost.UseHttpSys(delegate (Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions options)
			{
				options.UrlPrefixes.Clear();
				if (!string.IsNullOrWhiteSpace(httpSysPrefixes))
				{
					foreach (string prefix in httpSysPrefixes.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
					{
						string trimmed = prefix.Trim();
						if (trimmed.Length > 0)
						{
							options.UrlPrefixes.Add(trimmed);
						}
					}
				}
				if (options.UrlPrefixes.Count == 0)
				{
					foreach (string prefix in DefaultPrefixes())
					{
						options.UrlPrefixes.Add(prefix);
					}
				}
				Logger.Instance.Info("Listening on " + string.Join(", ", options.UrlPrefixes));
			});
		}
		webApplicationBuilder.Services.AddControllers();
		WebApplication webApplication = webApplicationBuilder.Build();
		webApplication.Use(OnlyAllowLocal);
		webApplication.Use(CaptureUnhandled);
		webApplication.UseRewriter(new RewriteOptions().Add(FixUrl));
		webApplication.MapControllers();
		if (processId != 0)
		{
			Thread thread = new Thread((ThreadStart)delegate
			{
				WatchProcessId(processId);
			});
			thread.IsBackground = true;
			thread.Start();
		}
		try
		{
			webApplication.Run();
		}
		finally
		{
			AppDomain.CurrentDomain.UnhandledException -= UnhandledExceptionHandler.Handle;
		}
	}
}
