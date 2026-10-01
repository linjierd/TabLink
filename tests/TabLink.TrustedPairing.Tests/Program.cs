using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabLink.Windows;

var root=Path.Combine(AppContext.BaseDirectory,"test-artifacts","trusted-pairing-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions=0;
try
{
    var programData=Path.Combine(root,"ProgramData");
    var tabLink=Path.Combine(programData,"TabLink");
    var directory=Path.Combine(tabLink,"NativeTrust");
    var created=new List<string>();var verified=new List<string>();
    FileStream CreateProtectedFile(string path)
    {
        created.Add(Path.GetFileName(path));
        return new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    }
    NativeTrustStorageBoundary Boundary(Func<string,FileStream>? createFile=null)=>new(programData,tabLink,directory,
        ()=>Directory.CreateDirectory(directory),
        createFile??CreateProtectedFile,
        path=>
        {
            verified.Add(Path.GetFileName(path));
            if(!File.Exists(path)||(File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
                throw new InvalidDataException("unsafe test file");
        },bytes=>bytes.Select(value=>(byte)(value^0x5a)).ToArray(),
        bytes=>bytes.Select(value=>(byte)(value^0x5a)).ToArray());

    string hostId;
    using var device=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var spki=Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
    var deviceId=TrustedPairingProtocol.DeviceIdFromPublicKey(spki);
    using(var first=new NativeTrustRepository(Boundary()))
    {
        hostId=first.HostId;
        Check(first.Count==0,"new repository is empty");
        using var certificate=first.CreateServerCertificate();
        Check(certificate.HasPrivateKey&&Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant()==hostId,
            "server certificate keeps installation identity");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var endpoint=(IPEndPoint)listener.LocalEndpoint;
            var serverHandshake=Task.Run(async()=>
            {
                using var accepted=await listener.AcceptTcpClientAsync();
                using var tls=new SslStream(accepted.GetStream(),false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate=certificate,ClientCertificateRequired=false,
                    EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                    CertificateRevocationCheckMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                });
            });
            using var client=new TcpClient();await client.ConnectAsync(endpoint.Address,endpoint.Port);
            using var clientTls=new SslStream(client.GetStream(),false,(_,remote,_,_)=>(remote is not null&&
                Convert.ToHexString(SHA256.HashData(remote.GetRawCertData())).ToLowerInvariant()==hostId));
            await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost="TabLink",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,
                CertificateRevocationCheckMode=System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            });
            await serverHandshake;
            Check(clientTls.IsAuthenticated&&clientTls.IsEncrypted,"persisted host certificate completes a Schannel TLS handshake");
        }
        finally{listener.Stop();}
        first.Register(deviceId,spki,"测试平板");
        var challenge=TrustedPairingProtocol.CreateChallenge();
        var signature=device.SignData(TrustedPairingProtocol.BuildProofTranscript(hostId,deviceId,challenge),
            HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);
        Check(first.Verify(deviceId,challenge,signature),"fresh registered signature verifies");
        var changed=(byte[])challenge.Clone();changed[0]^=1;
        Check(!first.Verify(deviceId,changed,signature),"changed challenge rejects replayed signature");
        using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrong=other.SignData(TrustedPairingProtocol.BuildProofTranscript(hostId,deviceId,challenge),
            HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);
        Check(!first.Verify(deviceId,challenge,wrong),"wrong private key is rejected");
    }
    using(var reopened=new NativeTrustRepository(Boundary()))
    {
        Check(reopened.HostId==hostId&&reopened.Count==1,"identity and trust survive restart");
        var item=reopened.Snapshot().Single();
        Check(item.DisplayName=="测试平板"&&reopened.Revoke(item.DeviceId)&&reopened.Count==0,
            "trusted device is listed and revocable");
    }
    Check(created.Contains("host-identity.dpapi")&&created.Any(name=>name.StartsWith(".trusted-devices.",StringComparison.Ordinal)),
        "identity and atomic registry files use protected creation boundary");
    Check(verified.Contains("host-identity.dpapi")&&verified.Contains("trusted-devices.json"),
        "committed identity and registry are reverified");

    var writeFailure=false;
    FileStream FailRegistryWrites(string path)
    {
        if(writeFailure&&Path.GetFileName(path).StartsWith(".trusted-devices.",StringComparison.Ordinal))
            throw new IOException("injected protected registry write failure");
        return CreateProtectedFile(path);
    }
    using(var failedRegister=new NativeTrustRepository(Boundary(FailRegistryWrites)))
    {
        writeFailure=true;
        Throws<IOException>(()=>failedRegister.Register(deviceId,spki,"不应进入内存"),"failed registration surfaces protected write failure");
        var challenge=TrustedPairingProtocol.CreateChallenge();
        var signature=device.SignData(TrustedPairingProtocol.BuildProofTranscript(hostId,deviceId,challenge),
            HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);
        Check(failedRegister.Count==0&&!failedRegister.Contains(deviceId)&&!failedRegister.Verify(deviceId,challenge,signature),
            "failed registration is not accepted from process memory");
        writeFailure=false;
    }
    using(var afterFailedRegister=new NativeTrustRepository(Boundary()))
        Check(afterFailedRegister.Count==0,"failed registration is not persisted");

    using(var seed=new NativeTrustRepository(Boundary()))seed.Register(deviceId,spki,"回滚测试");
    using(var failedMutation=new NativeTrustRepository(Boundary(FailRegistryWrites)))
    {
        var before=failedMutation.Snapshot().Single();
        var challenge=TrustedPairingProtocol.CreateChallenge();
        var signature=device.SignData(TrustedPairingProtocol.BuildProofTranscript(hostId,deviceId,challenge),
            HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence);
        writeFailure=true;
        Throws<IOException>(()=>failedMutation.Verify(deviceId,challenge,signature),"failed last-used update surfaces protected write failure");
        Check(failedMutation.Snapshot().Single().LastUsedUtc==before.LastUsedUtc,
            "failed verification metadata write does not publish an in-memory timestamp");
        Throws<IOException>(()=>failedMutation.Revoke(deviceId),"failed revocation surfaces protected write failure");
        Check(failedMutation.Count==1&&failedMutation.Contains(deviceId),
            "failed revocation keeps the trusted device in process memory");
        writeFailure=false;
    }
    using(var afterFailedMutation=new NativeTrustRepository(Boundary()))
    {
        Check(afterFailedMutation.Count==1&&afterFailedMutation.Contains(deviceId),
            "failed verification and revocation leave the committed registry unchanged");
        Check(afterFailedMutation.Revoke(deviceId),"rollback test trust can still be revoked after storage recovers");
    }

    var validRegistry=File.ReadAllBytes(Path.Combine(directory,"trusted-devices.json"));
    try
    {
        RejectRegistry(Encoding.UTF8.GetBytes("{]"),typeof(JsonException),"malformed registry JSON is reported as invalid protected data");
        RejectRegistry(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"devices\":[]}"),null,
            "duplicate registry fields are rejected");
        RejectRegistry(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"devices\":[],\"token\":\"forbidden\"}"),null,
            "unknown registry fields are rejected");
        var now=DateTimeOffset.UtcNow.ToString("O");
        RejectRegistry(Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"devices\":[{{\"deviceId\":\"{deviceId}\",\"displayName\":\"Tablet\",\"publicKeySpki\":\"!\",\"createdUtc\":\"{now}\",\"lastUsedUtc\":\"{now}\"}}]}}"),
            typeof(FormatException),"invalid registry public key is reported as invalid protected data");
        var future=DateTimeOffset.UtcNow.AddDays(1).ToString("O");
        RejectRegistry(Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"devices\":[{{\"deviceId\":\"{deviceId}\",\"displayName\":\"Tablet\",\"publicKeySpki\":\"{spki}\",\"createdUtc\":\"{now}\",\"lastUsedUtc\":\"{future}\"}}]}}"),
            null,"future last-used timestamps are rejected");
    }
    finally{File.WriteAllBytes(Path.Combine(directory,"trusted-devices.json"),validRegistry);}

    var unknownTemporary=Path.Combine(directory,".trusted-devices.not-a-guid.tmp");
    File.WriteAllText(unknownTemporary,"x");
    try{Throws<InvalidDataException>(()=>{using var repository=new NativeTrustRepository(Boundary());},"unknown temporary files fail closed");}
    finally{File.Delete(unknownTemporary);}

    using(var key=ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
        var id=TrustedPairingProtocol.DeviceIdFromPublicKey(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var valid=Encoding.UTF8.GetBytes($"{{\"protocol\":1,\"token\":\"{new string('a',64)}\",\"features\":[\"trusted-device-v1\"],\"deviceId\":\"{id}\",\"devicePublicKey\":\"{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}\",\"deviceName\":\"Tablet\"}}");
        Check(TrustedPairingProtocol.ParseInitialHello(valid).DeviceId==id,"strict registration hello parses");
        Reject(()=>TrustedPairingProtocol.ParseInitialHello(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"protocol\":1","\"protocol\":1,\"protocol\":1"))),"duplicate hello field rejected");
        Reject(()=>TrustedPairingProtocol.ParseInitialHello(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"deviceName\":\"Tablet\"","\"deviceName\":\"Tablet\",\"secret\":\"x\""))),"unknown hello field rejected");
    }
    var discoveryHost=new string('b',64);
    var request=Encoding.UTF8.GetBytes($"{{\"v\":1,\"type\":\"discover\",\"hostId\":\"{discoveryHost}\",\"nonce\":\"{new string('c',32)}\"}}");
    Check(NativeDiscoveryService.ParseRequest(request).HostId==discoveryHost,"strict discovery request parses");
    var malformedRequest=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(request).Replace("}",",\"token\":\"forbidden\"}"));
    Reject(()=>NativeDiscoveryService.ParseRequest(malformedRequest),"discovery secret field rejected");
    Check(NativeDiscoveryService.IsSameSubnet(IPAddress.Parse("192.168.8.20"),IPAddress.Parse("192.168.8.1"),24),"same subnet accepted");
    Check(!NativeDiscoveryService.IsSameSubnet(IPAddress.Parse("192.168.9.20"),IPAddress.Parse("192.168.8.1"),24),"different subnet rejected");
    long discoveryClock=1000;
    var sourceAddress=IPAddress.Parse("192.168.8.20");
    var discoveryLimiter=new DiscoveryResponseRateLimiter(()=>discoveryClock);
    for(var index=0;index<32;index++)
        Check(!NativeDiscoveryService.TryAuthorizeRequest(malformedRequest,discoveryHost,sourceAddress,discoveryLimiter,out _),
            "malformed discovery request rejected before rate limiting");
    Check(discoveryLimiter.TrackedAddressCount==0,"malformed discovery traffic consumes no tracked-address capacity");
    Check(!NativeDiscoveryService.TryAuthorizeRequest(request,new string('d',64),sourceAddress,discoveryLimiter,out _)&&
          discoveryLimiter.TrackedAddressCount==0,"wrong-host discovery request consumes no response quota");
    for(var index=0;index<DiscoveryResponseRateLimiter.MaximumResponsesPerWindow;index++)
        Check(NativeDiscoveryService.TryAuthorizeRequest(request,discoveryHost,sourceAddress,discoveryLimiter,out _),
            "valid discovery request accepted within response limit");
    Check(!NativeDiscoveryService.TryAuthorizeRequest(request,discoveryHost,sourceAddress,discoveryLimiter,out _),
        "valid discovery response rate remains bounded per source address");
    discoveryClock+=10001;
    Check(NativeDiscoveryService.TryAuthorizeRequest(request,discoveryHost,sourceAddress,discoveryLimiter,out _),
        "valid discovery response quota recovers after its time window");
    var boundedLimiter=new DiscoveryResponseRateLimiter(()=>discoveryClock);
    var boundedCapacity=Enumerable.Range(0,DiscoveryResponseRateLimiter.MaximumTrackedAddresses).All(index=>
        NativeDiscoveryService.TryAuthorizeRequest(request,discoveryHost,
            IPAddress.Parse($"10.0.{index}.1"),boundedLimiter,out _));
    Check(boundedCapacity&&boundedLimiter.TrackedAddressCount==DiscoveryResponseRateLimiter.MaximumTrackedAddresses&&
          !NativeDiscoveryService.TryAuthorizeRequest(request,discoveryHost,IPAddress.Parse("10.1.0.1"),boundedLimiter,out _),
        "discovery rate tracking remains capped at its fixed address limit");
    Check(NativeDiscoveryService.WaitForReceiveRecoveryAsync(new SocketException((int)SocketError.ConnectionReset),CancellationToken.None).IsCompletedSuccessfully&&
          NativeDiscoveryService.WaitForReceiveRecoveryAsync(new SocketException((int)SocketError.MessageSize),CancellationToken.None).IsCompletedSuccessfully,
        "transient UDP receive errors resume without delay");
    using(var retryCancellation=new CancellationTokenSource())
    {
        var retry=NativeDiscoveryService.WaitForReceiveRecoveryAsync(new SocketException((int)SocketError.NetworkDown),retryCancellation.Token);
        Check(NativeDiscoveryService.ReceiveFailureBackoff>TimeSpan.Zero&&NativeDiscoveryService.ReceiveFailureBackoff<=TimeSpan.FromSeconds(1)&&!retry.IsCompleted,
            "other UDP receive errors use a finite non-busy retry delay");
        retryCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async()=>await retry,"UDP receive retry delay stops immediately on cancellation");
    }
    var vectorChallenge=Enumerable.Range(0,32).Select(value=>(byte)value).ToArray();
    Check(Convert.ToHexString(SHA256.HashData(TrustedPairingProtocol.BuildProofTranscript(
        string.Concat(Enumerable.Repeat("01",32)),string.Concat(Enumerable.Repeat("02",32)),vectorChallenge)))==
        "1F57A15130EE260C4242840D79E543CFA0843976E42B0989C628D250E305AFB9",
        "cross-platform trusted transcript vector changed");
    Console.WriteLine($"TrustedPairing: {assertions} assertions passed");

    void RejectRegistry(byte[] payload,Type? expectedInner,string message)
    {
        File.WriteAllBytes(Path.Combine(directory,"trusted-devices.json"),payload);
        assertions++;
        try{using var repository=new NativeTrustRepository(Boundary());}
        catch(InvalidDataException error)
        {
            if(expectedInner is null||(error.InnerException is not null&&expectedInner.IsAssignableFrom(error.InnerException.GetType())))return;
            throw new InvalidOperationException(message+" (unexpected inner exception)",error);
        }
        catch(Exception error){throw new InvalidOperationException(message+" (unexpected exception type)",error);}
        throw new InvalidOperationException(message);
    }
}
finally
{
    var full=Path.GetFullPath(root);
    var expected=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"test-artifacts"))+Path.DirectorySeparatorChar;
    if(full.StartsWith(expected,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("trusted-pairing-",StringComparison.Ordinal))
        Directory.Delete(full,true);
}

void Check(bool condition,string message){assertions++;if(!condition)throw new InvalidOperationException(message);}
async Task ThrowsAsync<T>(Func<Task> action,string message)where T:Exception
{
    assertions++;
    try{await action();}
    catch(T){return;}
    catch(Exception error){throw new InvalidOperationException(message+" (unexpected exception type)",error);}
    throw new InvalidOperationException(message);
}
void Throws<T>(Action action,string message)where T:Exception
{
    assertions++;
    try{action();}
    catch(T){return;}
    catch(Exception error){throw new InvalidOperationException(message+" (unexpected exception type)",error);}
    throw new InvalidOperationException(message);
}
void Reject(Action action,string message)
{
    assertions++;
    try{action();throw new InvalidOperationException(message);}
    catch(InvalidDataException){}
}
