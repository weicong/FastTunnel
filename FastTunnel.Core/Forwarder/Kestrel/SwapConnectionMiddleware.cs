// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Forwarder.Kestrel;

internal class SwapConnectionMiddleware
{
    readonly ConnectionDelegate next;
    readonly ILogger<SwapConnectionMiddleware> logger;
    FastTunnelServer fastTunnelServer;

    public SwapConnectionMiddleware(ConnectionDelegate next, ILogger<SwapConnectionMiddleware> logger, FastTunnelServer fastTunnelServer)
    {
        this.next = next;
        this.logger = logger;
        this.fastTunnelServer = fastTunnelServer;
    }

    internal async Task OnConnectionAsync(ConnectionContext context)
    {
        var ctx = context as FastTunnelConnectionContext;
        if (ctx != null && ctx.IsFastTunnel)
        {
            if (ctx.Method == "PROXY")
            {
                await doSwap(ctx);
            }
            else if (ctx.MatchWeb != null)
            {
                await waitSwap(ctx);
            }
            else
            {
                throw new NotSupportedException();
            }


        }
        else
        {
            await next(context);
        }
    }

    private async Task waitSwap(FastTunnelConnectionContext context)
    {
        var requestId = Guid.NewGuid().ToString().Replace("-", "");
        var web = context.MatchWeb;

        TaskCompletionSource<Stream> tcs = new();
        logger.LogDebug($"[Http]Swap开始 {requestId}|{context.Host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}");
        tcs.SetTimeOut(10000, () => { logger.LogDebug($"[Proxy TimeOut]:{requestId}"); });

        fastTunnelServer.ResponseTasks.TryAdd(requestId, tcs);

        try
        {
            try
            {
                // 发送指令给客户端，等待建立隧道
                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{requestId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", default);
            }
            catch (WebSocketException)
            {
                web.LogOut();

                // 通讯异常，返回客户端离线
                throw new Exception("客户端离线");
            }

            using var res = await tcs.Task;
            using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);

            var t1 = res.CopyToAsync(reverseConnection);
            var t2 = reverseConnection.CopyToAsync(res);

            await Task.WhenAll(t1, t2);

            logger.LogDebug("[Http]Swap结束");
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            fastTunnelServer.ResponseTasks.TryRemove(requestId, out _);
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
        }
    }

    private async Task doSwap(FastTunnelConnectionContext context)
    {
        var requestId = context.MessageId;
        if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseStream))
        {
            throw new Exception($"[PROXY]:RequestId不存在 {requestId}");
        };

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseStream.TrySetResult(reverseConnection);

        var lifetime = context.Features.Get<IConnectionLifetimeFeature>();

        var closedAwaiter = new TaskCompletionSource<object>();

        lifetime.ConnectionClosed.Register((task) =>
        {
            (task as TaskCompletionSource<object>).SetResult(null);
        }, closedAwaiter);

        try
        {
            await closedAwaiter.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "");
        }
        finally
        {
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
            logger.LogInformation($"=====================Swap End:{requestId}================== ");
        }
    }

    private async Task Swap(ReadOnlySequence<byte> buffer, SequencePosition position, ConnectionContext context)
    {
        var firstLineBuffer = buffer.Slice(0, position);
        var firstLine = Encoding.UTF8.GetString(firstLineBuffer);

        // SWAP /c74eb488a0f54d888e63d85c67428b52 HTTP/1.1
        var endIndex = firstLine.IndexOf(" ", 6);
        var requestId = firstLine.Substring(6, endIndex - 6);
        Console.WriteLine($"[开始进行Swap操作] {requestId}");

        context.Transport.Input.AdvanceTo(buffer.GetPosition(1, position), buffer.GetPosition(1, position));

        if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseForYarp))
        {
            logger.LogError($"[PROXY]:RequestId不存在 {requestId}");
            return;
        };

        using var reverseConnection = new DuplexPipeStream(context.Transport.Input, context.Transport.Output, true);
        responseForYarp.TrySetResult(reverseConnection);

        var lifetime = context.Features.Get<IConnectionLifetimeFeature>();

        var closedAwaiter = new TaskCompletionSource<object>();

        lifetime.ConnectionClosed.Register((task) =>
        {
            (task as TaskCompletionSource<object>).SetResult(null);
        }, closedAwaiter);

        try
        {
            await closedAwaiter.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "");
        }
        finally
        {
            context.Transport.Input.Complete();
            context.Transport.Output.Complete();
            logger.LogInformation($"=====================Swap End:{requestId}================== ");
        }
    }
}