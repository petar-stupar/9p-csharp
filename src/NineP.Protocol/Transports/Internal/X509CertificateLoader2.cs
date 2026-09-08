using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// The one place a <see cref="X509Certificate"/> from a callback becomes an
/// <see cref="X509Certificate2"/>. The conversion differs by target framework and the older form
/// is obsolete from .NET 9, so it is written once here rather than at every call site.
/// </summary>
internal static class X509CertificateLoader2
{
    /// <summary>Copies a callback's certificate into one this library owns.</summary>
    /// <param name="certificate">The certificate the platform handed to the callback.</param>
    /// <returns>An owned copy.</returns>
    public static X509Certificate2 FromCertificate(X509Certificate certificate) =>
        LoadCopy(certificate.Export(X509ContentType.Cert));

    private static X509Certificate2 LoadCopy(byte[] der) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadCertificate(der);
#else
        new X509Certificate2(der);
#endif
}
