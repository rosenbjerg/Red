﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Red.Interfaces;

namespace Red
{
    public partial class RedHttpServer
    {
        private const string GetMethod = "GET";
        private const string PostMethod = "POST";
        private const string PutMethod = "PUT";
        private const string DeleteMethod = "DELETE";
        
        private readonly List<IRedExtension> _plugins = new List<IRedExtension>();

        private IWebHost? _host;
        private readonly string? _publicRoot;
        
        private IWebHost Build(IReadOnlyCollection<string> hostnames)
        {
            if (_host != default)
            {
                throw new RedHttpServerException("The server is already running");
            }
            Initialize();
            var urls = hostnames.Count != 0 
                ? hostnames.Select(url => $"http://{url}:{Port}").ToArray()
                : new []{ $"http://localhost:{Port}" };
            return new WebHostBuilder()
                .UseKestrel()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    ConfigureServices?.Invoke(services);
                })
                .Configure(app =>
                {
                    if (!string.IsNullOrWhiteSpace(_publicRoot) && Directory.Exists(_publicRoot))
                        app.UseFileServer(new FileServerOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath(_publicRoot)) });
                    if (_useWebSockets)
                        app.UseWebSockets();
                    app.UseRouter(routeBuilder =>
                    {
                        foreach (var route in _routes)
                            route(routeBuilder);
                        _routes.Clear();
                    });
                    ConfigureApplication?.Invoke(app);
                })
                .UseUrls(urls)
                .Build();
        }
        
        private void Initialize()
        {
            foreach (var plugin in _plugins)
            {
                plugin.Initialize(this);
            }
        }
        
        private readonly List<Action<IRouteBuilder>> _routes = new List<Action<IRouteBuilder>>();
        private static readonly Regex NamePathParameterRegex = new Regex(":[\\w-]+", RegexOptions.Compiled);
        private bool _useWebSockets = false;


        private async Task<HandlerType> ExecuteHandler(HttpContext aspNetContext, IEnumerable<Func<Request, Response, Task<HandlerType>>> handlers)
        {
            var context = new Context(aspNetContext, Plugins);
            
            var status = HandlerType.Continue;
            try
            {
                foreach (var middleware in _middle.Concat(handlers))
                {
                    status = await middleware(context.Request, context.Response);
                    if (status != HandlerType.Continue) return status;
                }
                
                return status;
            }
            catch (Exception e)
            { 
                return await HandleException(context, status, e);
            }
        }
        private async Task<HandlerType> ExecuteHandler(HttpContext aspNetContext, IEnumerable<Func<Request, Response, WebSocketDialog, Task<HandlerType>>> handlers)
        {
            var context = new Context(aspNetContext, Plugins);
            
            var status = HandlerType.Continue;
            try
            {
                if (aspNetContext.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await aspNetContext.WebSockets.AcceptWebSocketAsync();
                    var webSocketDialog = new WebSocketDialog(webSocket);
                    
                    foreach (var middleware in _wsMiddle.Concat(handlers))
                    {
                        status = await middleware(context.Request, context.Response, webSocketDialog);
                        if (status != HandlerType.Continue) return status;
                    }
                    
                    await webSocketDialog.ReadFromWebSocket();
                    return status;
                }

                await context.Response.SendStatus(HttpStatusCode.BadRequest);
                return HandlerType.Error;
            }
            catch (Exception e)
            {
                return await HandleException(context, status, e);
            }
        }

        private async Task<HandlerType> HandleException(Context context, HandlerType status, Exception e)
        {
            var path = context.Request.AspNetRequest.Path.ToString();
            var method = context.Request.AspNetRequest.Method;
            OnHandlerException?.Invoke(this, new HandlerExceptionEventArgs(method, path, e));
            
            if (status != HandlerType.Continue)
                return HandlerType.Error;
                
            if (RespondWithExceptionDetails)
                await context.Response.SendString(e.ToString(), status: HttpStatusCode.InternalServerError);
            else
                await context.Response.SendStatus(HttpStatusCode.InternalServerError);
            
            return HandlerType.Error;
        }

        private void AddHandlers(string route, string method, IReadOnlyCollection<Func<Request, Response, Task<HandlerType>>> handlers)
        {
            if (handlers.Count == 0)
                throw new RedHttpServerException("A route requires at least one handler");
            
            var path = ConvertPathParameters(route, NamePathParameterRegex);
            _routes.Add(routeBuilder => routeBuilder.MapVerb(method, path, ctx => ExecuteHandler(ctx, handlers)));
        }
        private void AddHandlers(string route, IReadOnlyCollection<Func<Request, Response, WebSocketDialog, Task<HandlerType>>> handlers)
        {
            if (handlers.Count == 0)
                throw new RedHttpServerException("A route requires at least one handler");
            
            var path = ConvertPathParameters(route, NamePathParameterRegex);
            _routes.Add(routeBuilder => routeBuilder.MapGet(path, ctx => ExecuteHandler(ctx, handlers)));
        }
        private static string ConvertPathParameters(string parameter, Regex urlParam)
        {
            return urlParam
                .Replace(parameter, match => "{" + match.Value.TrimStart(':') + "}")
                .Replace("*", "{*any}")
                .Trim('/');
        }

    }
}