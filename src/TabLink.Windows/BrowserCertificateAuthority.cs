using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TabLink.Windows;

// Installation-scoped CA. Only the public certificate is exported for manual
// device trust. The private PFX is DPAPI-encrypted for this Windows user.
internal sealed class BrowserCertificateAuthority : IDisposable
{
    readonly X509Certificate2 certificate;
    internal string PublicCertificatePath { get; }
    internal string Fingerprint { get; }

    internal BrowserCertificateAuthority(string? storageDirectory=null)
    {
        var directory=storageDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TabLink","Browser","pki");
        Directory.CreateDirectory(directory);
        PublicCertificatePath=Path.Combine(directory,"TabLink-Browser-CA.cer");
        var secretPath=Path.Combine(directory,"ca.dpapi");
        using var ownership=new Mutex(false,"Local\\TabLink-Browser-CA-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory)))));
        var acquired=false;
        try
        {
            try {acquired=ownership.WaitOne(TimeSpan.FromSeconds(10));}
            catch(AbandonedMutexException){acquired=true;}
            if(!acquired)throw new IOException("浏览器证书正在初始化，请稍后重试。");
            if(File.Exists(secretPath))
            {
                if(new FileInfo(secretPath).Length>65536)throw new InvalidDataException("浏览器证书文件无效。");
                var protectedBytes=File.ReadAllBytes(secretPath);
                var pfx=ProtectedData.Unprotect(protectedBytes,null,DataProtectionScope.CurrentUser);
                try {certificate=X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable);}
                finally {CryptographicOperations.ZeroMemory(pfx);}
                if(!certificate.HasPrivateKey || certificate.NotAfter<=DateTime.UtcNow.AddDays(1) ||
                    !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x=>x.CertificateAuthority))
                    throw new InvalidDataException("浏览器 CA 已失效；请在电脑端重新配置并重新信任证书。");
            }
            else
            {
                using var key=RSA.Create(3072);
                var request=new CertificateRequest("CN=TabLink Browser Local CA "+Guid.NewGuid().ToString("N")[..8],key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,true,0,true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
                request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
                using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5));
                var pfx=generated.Export(X509ContentType.Pfx);
                try
                {
                    var encrypted=ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser);
                    using(var file=new FileStream(secretPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(encrypted);
                    certificate=X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable);
                }
                finally {CryptographicOperations.ZeroMemory(pfx);}
            }
            File.WriteAllBytes(PublicCertificatePath,certificate.RawData);
            Fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        }
        catch {certificate?.Dispose();throw;}
        finally {if(acquired)ownership.ReleaseMutex();}
    }

    internal X509Certificate2 CreateServerCertificate(IPAddress address)
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=TabLink Browser Host",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
        var names=new SubjectAlternativeNameBuilder();names.AddIpAddress(address);request.CertificateExtensions.Add(names.Build());
        using var issued=request.Create(certificate,DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(30),RandomNumberGenerator.GetBytes(16));
        using var withKey=issued.CopyWithPrivateKey(key);
        var pfx=withKey.Export(X509ContentType.Pfx);
        try {return X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet);}
        finally {CryptographicOperations.ZeroMemory(pfx);}
    }
    public void Dispose()=>certificate.Dispose();
}
