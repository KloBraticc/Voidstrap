using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using VoidstrapClient.Common;

namespace VoidstrapClient.ServerAuthInterface.Web;

internal static class Server
{
	private static IResult V1ChallengeHandler(HttpContext context)
	{
		IPAddress iPAddress = context.Connection.RemoteIpAddress ?? IPAddress.None;
		if (!AuthService.Instance.TryCreateChallenge(iPAddress, out string challenge))
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}
		return Results.Ok(new { challenge });
	}

	private static IResult V1AuthHandler(HttpContext context)
	{
		IPAddress iPAddress = context.Connection.RemoteIpAddress ?? IPAddress.None;
		if (AuthService.Instance.IsIPAuthorised(iPAddress))
		{
			return Results.Ok();
		}
		string proof = context.Request.Headers["X-Voidstrap-Auth-Proof"].ToString();
		if (AuthService.Instance.TryConsumeChallenge(iPAddress, out string challenge) && KeyService.Instance.ValidateProofThenInvalidateKey(challenge, proof))
		{
			if (!AuthService.Instance.TryAuthoriseIP(iPAddress))
			{
				return Results.StatusCode(StatusCodes.Status429TooManyRequests);
			}
			Logger.Instance.Info($"{iPAddress}: Authorized");
			return Results.Ok();
		}
		Logger.Instance.Warn($"{iPAddress}: Authorization failed");
		return Results.Unauthorized();
	}

	private static object V1IsAuthHandler(HttpContext context)
	{
		return AuthService.Instance.IsIPAuthorised(context.Connection.RemoteIpAddress ?? IPAddress.None);
	}

	public static async Task Start(CancellationToken token)
	{
		WebApplicationBuilder webApplicationBuilder = WebApplication.CreateBuilder();
		webApplicationBuilder.WebHost.ConfigureKestrel(delegate(KestrelServerOptions k)
		{
			k.ListenAnyIP(Config.Default.WebServerPort);
		});
		await using WebApplication webApplication = webApplicationBuilder.Build();
		webApplication.UseRouting();
		webApplication.MapGet("/v1/auth/challenge", new Func<HttpContext, IResult>(V1ChallengeHandler));
		webApplication.MapPost("/v1/auth", new Func<HttpContext, IResult>(V1AuthHandler));
		webApplication.MapGet("/v1/is-auth", new Func<HttpContext, object>(V1IsAuthHandler));
		await webApplication.StartAsync(token);
		try
		{
			await webApplication.WaitForShutdownAsync(token);
		}
		finally
		{
			await webApplication.StopAsync(CancellationToken.None);
		}
	}
}
