using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TabLink.Core;

namespace TabLink.Windows;

internal sealed class BrowserHost : IAsyncDisposable
{
    public const int DefaultPort=27185;
    // SIPSorcery requires even RTP ports even when RTCP is multiplexed.
    public static IReadOnlyList<int> MediaPorts { get; }=Array.AsReadOnly(Enumerable.Range(0,8).Select(i=>27200+i*2).ToArray());
    readonly IPAddress address;
    readonly int port;
    readonly Func<Guid,TabletDisplayProfile,CancellationToken,Task<BrowserDisplaySession>> prepare;
    readonly BrowserCertificateAuthority authority;
    readonly X509Certificate2 serverCertificate;
    readonly CancellationTokenSource lifetime=new();
    readonly ConcurrentDictionary<Guid,BrowserRtcSession> sessions=new();
    readonly ConcurrentDictionary<string,DateTimeOffset> pairings=new(StringComparer.Ordinal);
    readonly object gate=new();
    WebApplication? application;
    Task? disposal;
    int pendingSockets,sessionSlot;
    public string CaCertificatePath=>authority.PublicCertificatePath;
    public string CaFingerprint=>authority.Fingerprint;
    public string BaseUri=>$"https://{address}:{port}";
    public int ActiveSessionCount=>sessions.Count;
    public event Action<Guid>? FramePresented;
    public event Action<BrowserSessionStatus>? SessionChanged;
    public event Action<Guid,string>? Diagnostic;

    public BrowserHost(IPAddress address,Func<Guid,TabletDisplayProfile,CancellationToken,Task<BrowserDisplaySession>> prepare,
        int maxClients=1,int port=DefaultPort):this(address,prepare,maxClients,port,null){}

    internal BrowserHost(IPAddress address,Func<Guid,TabletDisplayProfile,CancellationToken,Task<BrowserDisplaySession>> prepare,
        int maxClients,int port,string? certificateDirectory)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.GetAddressBytes()[0]>=224 ||
            (!address.Equals(IPAddress.Loopback)&&!NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up)
                .SelectMany(x=>x.GetIPProperties().UnicastAddresses).Any(x=>x.Address.Equals(address))))
            throw new ArgumentException("请选择此电脑的明确 IPv4 地址。",nameof(address));
        if(maxClients!=1)throw new ArgumentOutOfRangeException(nameof(maxClients),"TabLink 浏览器接入只允许一个副屏客户端。");
        if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        this.address=address;this.prepare=prepare;this.port=port;
        authority=new(certificateDirectory);
        try {serverCertificate=authority.CreateServerCertificate(address);}
        catch {authority.Dispose();throw;}
    }

    public async Task StartAsync(CancellationToken ct=default)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null,this);
            if(application is not null)throw new InvalidOperationException("浏览器服务已经启动。");
            var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],ApplicationName=typeof(BrowserHost).Assembly.FullName,ContentRootPath=AppContext.BaseDirectory});
            builder.Logging.ClearProviders(); // Do not log pairing URLs, SDP or access tokens.
            builder.WebHost.ConfigureKestrel(options=>
            {
                options.AddServerHeader=false;
                options.Limits.MaxConcurrentConnections=32;
                options.Limits.MaxRequestBodySize=65536;
                options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout=TimeSpan.FromSeconds(30);
                options.Listen(address,port,listen=>listen.UseHttps(https=>
                {https.ServerCertificate=serverCertificate;https.SslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13;}));
            });
            application=builder.Build();
            application.Use(async(context,next)=>
            {
                if(context.Request.Host.Value!=$"{address}:{port}"){context.Response.StatusCode=400;return;}
                context.Response.Headers.CacheControl="no-store";
                context.Response.Headers["Referrer-Policy"]="no-referrer";
                context.Response.Headers["X-Content-Type-Options"]="nosniff";
                context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                await next();
            });
            application.UseWebSockets(new WebSocketOptions{KeepAliveInterval=TimeSpan.FromSeconds(10)});
            application.MapGet("/",context=>SendAsset(context,"index.html","text/html; charset=utf-8"));
            application.MapGet("/client.js",context=>SendAsset(context,"client.js","text/javascript; charset=utf-8"));
            application.MapGet("/style.css",context=>SendAsset(context,"style.css","text/css; charset=utf-8"));
            application.MapGet("/ca.cer",async context=>
            {
                context.Response.ContentType="application/pkix-cert";
                context.Response.Headers.ContentDisposition="attachment; filename=TabLink-Browser-CA.cer";
                await context.Response.SendFileAsync(CaCertificatePath,context.RequestAborted);
            });
            application.Map("/signal",HandleSignal);
        }
        try {await application.StartAsync(ct);}
        catch {await DisposeAsync();throw;}
    }

    public BrowserPairingOffer CreatePairing()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null,this);
            foreach(var expired in pairings.Where(x=>x.Value<=DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())pairings.TryRemove(expired,out _);
            if(pairings.Count>=32)throw new InvalidOperationException("待连接二维码过多，请等待旧二维码过期。");
            var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires=DateTimeOffset.UtcNow.AddMinutes(5);
            if(!pairings.TryAdd(TokenHash(token),expires))throw new IOException("无法生成唯一的浏览器配对令牌。");
            return new(BaseUri+"/#token="+token,expires);
        }
    }

    static string TokenHash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    bool ClaimToken(string token)
    {
        if(token.Length!=64||token.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))return false;
        if(lifetime.IsCancellationRequested)return false;
        var hash=TokenHash(token);
        return pairings.TryRemove(hash,out var expiry)&&expiry>DateTimeOffset.UtcNow;
    }

    async Task HandleSignal(HttpContext context)
    {
        if(!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.Count!=1 || context.Request.Headers.Origin[0]!=BaseUri)
        {context.Response.StatusCode=403;return;}
        if(Interlocked.Increment(ref pendingSockets)>16){Interlocked.Decrement(ref pendingSockets);context.Response.StatusCode=429;return;}
        BrowserRtcSession? session=null;
        var slotReserved=false;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,context.RequestAborted);
        try
        {
            using var socket=await context.WebSockets.AcceptWebSocketAsync();
            using var auth=CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            auth.CancelAfter(TimeSpan.FromSeconds(8));
            using var hello=await BrowserRtcSession.ReadJsonAsync(socket,16384,auth.Token);
            var root=hello.RootElement;
            if(root.GetProperty("type").GetString()!="hello")throw new InvalidDataException("缺少浏览器配对信息。");
            var profile=BrowserProfile.Parse(root.GetProperty("profile").GetRawText());
            var token=root.GetProperty("token").GetString()??"";
            if(Interlocked.CompareExchange(ref sessionSlot,1,0)!=0)
                throw new InvalidDataException("已有浏览器副屏连接，请先断开当前设备。");
            slotReserved=true;
            if(!ClaimToken(token))throw new InvalidDataException("二维码已过期、已使用或连接数已满，请在电脑端重新配对。");
            var id=Guid.NewGuid();
            var mediaPort=MediaPorts[0];
            session=new BrowserRtcSession(id,address,mediaPort,socket,profile,prepare,
                status=>SessionChanged?.Invoke(status),()=>FramePresented?.Invoke(id),message=>Diagnostic?.Invoke(id,message),linked.Token);
            if(!sessions.TryAdd(id,session))throw new InvalidOperationException("会话创建失败。");
            await session.RunAsync();
        }
        catch(Exception ex) when(ex is OperationCanceledException or WebSocketException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            if(session is not null)SessionChanged?.Invoke(new(session.Id,"closed","浏览器连接已结束。"));
        }
        finally
        {
            try
            {
                if(session is not null)
                {
                    try {await session.DisposeAsync();}
                    finally {sessions.TryRemove(session.Id,out _);}
                }
            }
            finally
            {
                if(slotReserved)Interlocked.Exchange(ref sessionSlot,0);
                Interlocked.Decrement(ref pendingSockets);
            }
        }
    }

    static async Task SendAsset(HttpContext context,string name,string contentType)
    {
        var assembly=typeof(BrowserHost).Assembly;
        var resource=assembly.GetManifestResourceNames().SingleOrDefault(x=>x.EndsWith(".BrowserAssets."+name,StringComparison.Ordinal));
        if(resource is null){context.Response.StatusCode=503;return;}
        await using var stream=assembly.GetManifestResourceStream(resource)!;
        context.Response.ContentType=contentType;
        await stream.CopyToAsync(context.Response.Body,context.RequestAborted);
    }

    public async Task StopSessionAsync(Guid id)
    {if(sessions.TryGetValue(id,out var session))await session.DisposeAsync();}
    public ValueTask DisposeAsync()
    {lock(gate)return new(disposal??=Task.Run(DisposeCore));}
    async Task DisposeCore()
    {
        lifetime.Cancel();
        lock(gate)pairings.Clear();
        List<Exception>? cleanupFailures=null;
        try
        {
            foreach(var session in sessions.Values)try{await session.DisposeAsync();}catch(Exception ex){(cleanupFailures??=[]).Add(ex);}
            if(application is not null)
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try {await application.StopAsync(stop.Token);}
                finally {await application.DisposeAsync();}
            }
        }
        finally {serverCertificate.Dispose();authority.Dispose();lifetime.Dispose();}
        if(cleanupFailures is {Count:>0})throw new AggregateException("一个或多个浏览器副屏未能完成精确回收。",cleanupFailures);
    }
}
