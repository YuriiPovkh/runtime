// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace System.Net.Http
{
    internal partial class CurlHandler : HttpMessageHandler
    {
        internal sealed class ClientCertificateProvider : IDisposable
        {
            internal readonly GCHandle _gcHandle;
            internal readonly Interop.Ssl.ClientCertCallback _callback;
            private readonly X509Certificate2Collection _clientCertificates;
            private SafeEvpPKeyHandle _privateKeyHandle;
            private SafeX509Handle _certHandle;

            internal ClientCertificateProvider(X509Certificate2Collection clientCertificates)
            {
                _gcHandle = GCHandle.Alloc(this);
                _callback = TlsClientCertCallback;
                _clientCertificates = clientCertificates;
            }

            private int TlsClientCertCallback(IntPtr ssl, out IntPtr certHandle, out IntPtr privateKeyHandle)
            {
                const int CertificateSet = 1, NoCertificateSet = 0, SuspendHandshake = -1;

                certHandle = IntPtr.Zero;
                privateKeyHandle = IntPtr.Zero;

                if (ssl == IntPtr.Zero)
                {
                    Debug.Fail("Expected valid SSL pointer");
                    EventSourceTrace("Invalid SSL pointer in callback");
                    return NoCertificateSet;
                }

                SafeSslHandle sslHandle = null;
                X509Chain chain = null;
                X509Certificate2 certificate = null;
                try
                {
                    sslHandle = new SafeSslHandle(ssl, ownsHandle: false);

                    ISet<string> issuerNames = GetRequestCertificateAuthorities(sslHandle);

                    if (_clientCertificates != null) // manual mode
                    {
                        // If there's one certificate, just use it. Otherwise, try to find the best one.
                        if (_clientCertificates.Count == 1)
                        {
                            certificate = _clientCertificates[0];
                            chain = TLSCertificateExtensions.BuildNewChain(certificate, includeClientApplicationPolicy: false);
                        }
                        else if (!_clientCertificates.TryFindClientCertificate(issuerNames, out certificate, out chain))
                        {
                            EventSourceTrace("No manual certificate or chain.");
                            return NoCertificateSet;
                        }
                    }
                    else if (!GetAutomaticClientCertificate(issuerNames, out certificate, out chain)) // automatic mode
                    {
                        EventSourceTrace("No automatic certificate or chain.");
                        return NoCertificateSet;
                    }

                    Interop.Crypto.CheckValidOpenSslHandle(certificate.Handle);
                    using (RSAOpenSsl rsa = certificate.GetRSAPrivateKey() as RSAOpenSsl)
                    {
                        if (rsa != null)
                        {
                            _privateKeyHandle = rsa.DuplicateKeyHandle();
                            EventSourceTrace("RSA key");
                        }
                        else
                        {
                            using (ECDsaOpenSsl ecdsa = certificate.GetECDsaPrivateKey() as ECDsaOpenSsl)
                            {
                                if (ecdsa != null)
                                {
                                    _privateKeyHandle = ecdsa.DuplicateKeyHandle();
                                    EventSourceTrace("ECDsa key");
                                }
                            }
                        }
                    }

                    if (_privateKeyHandle == null || _privateKeyHandle.IsInvalid)
                    {
                        EventSourceTrace("Invalid private key");
                        return NoCertificateSet;
                    }

                    _certHandle = Interop.Crypto.X509Duplicate(certificate.Handle);
                    Interop.Crypto.CheckValidOpenSslHandle(_certHandle);
                    if (chain != null)
                    {
                        for (int i = chain.ChainElements.Count - 2; i > 0; i--)
                        {
                            SafeX509Handle dupCertHandle = Interop.Crypto.X509Duplicate(chain.ChainElements[i].Certificate.Handle);
                            Interop.Crypto.CheckValidOpenSslHandle(dupCertHandle);
                            if (!Interop.Ssl.SslAddExtraChainCert(sslHandle, dupCertHandle))
                            {
                                EventSourceTrace("Failed to add extra chain certificate");
                                return SuspendHandshake;
                            }
                        }
                    }

                    certHandle = _certHandle.DangerousGetHandle();
                    privateKeyHandle = _privateKeyHandle.DangerousGetHandle();

                    EventSourceTrace("Client certificate set: {0}", certificate);
                    return CertificateSet;
                }
                finally
                {
                    if (_clientCertificates == null) certificate?.Dispose(); // only dispose cert if it's automatic / newly created
                    chain?.Dispose();
                    sslHandle?.Dispose();
                }
            }

            public void Dispose()
            {
                _gcHandle.Free();
                _privateKeyHandle?.Dispose();
                _certHandle?.Dispose();
            }

            private static ISet<string> GetRequestCertificateAuthorities(SafeSslHandle sslHandle)
            {
                using (SafeSharedX509NameStackHandle names = Interop.Ssl.SslGetClientCAList(sslHandle))
                {
                    // TODO: When https://github.com/dotnet/corefx/pull/2862 is available for use, 
                    // size this appropriately based on nameCount.
                    var clientAuthorityNames = new HashSet<string>();

                    if (!names.IsInvalid)
                    {
                        int nameCount = Interop.Crypto.GetX509NameStackFieldCount(names);
                        for (int i = 0; i < nameCount; i++)
                        {
                            using (SafeSharedX509NameHandle nameHandle = Interop.Crypto.GetX509NameStackField(names, i))
                            {
                                X500DistinguishedName dn = Interop.Crypto.LoadX500Name(nameHandle);
                                clientAuthorityNames.Add(dn.Name);
                            }
                        }
                    }

                    return clientAuthorityNames;
                }
            }

            private static bool GetAutomaticClientCertificate(ISet<string> allowedIssuers, out X509Certificate2 certificate, out X509Chain chain)
            {
                using (X509Store myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    // Get the certs from the store.
                    myStore.Open(OpenFlags.ReadOnly);
                    X509Certificate2Collection certs = myStore.Certificates;

                    // Find a matching one.
                    bool gotCert = certs.TryFindClientCertificate(allowedIssuers, out certificate, out chain);

                    // Dispose all but the matching cert.
                    for (int i = 0; i < certs.Count; i++)
                    {
                        X509Certificate2 cert = certs[i];
                        if (cert != certificate)
                        {
                            cert.Dispose();
                        }
                    }

                    // Return whether we got one.
                    return gotCert;
                }
            }
        }
    }
}
