//
// HttpWebRequestTest.cs - NUnit Test Cases for System.Net.HttpWebRequest
//
// Authors:
//   Lawrence Pit (loz@cable.a2000.nl)
//   Martin Willemoes Hansen (mwh@sysrq.dk)
//   Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Martin Willemoes Hansen
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com
//

using NUnit.Framework;
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

using Mono.Security.Authenticode;
using Mono.Security.Protocol.Tls;

namespace MonoTests.System.Net
{
	[TestFixture]
	public class HttpWebRequestTest
	{
		[Test]
		[Category("InetAccess")] 
		public void Sync ()
		{
			HttpWebRequest req = (HttpWebRequest) WebRequest.Create ("http://www.google.com");
			Assertion.AssertNotNull ("req:If Modified Since: ", req.IfModifiedSince);

			req.UserAgent = "MonoClient v1.0";
			Assertion.AssertEquals ("req Header 1", "User-Agent", req.Headers.GetKey (0));
			Assertion.AssertEquals ("req Header 2", "MonoClient v1.0", req.Headers.Get (0));

			HttpWebResponse res = (HttpWebResponse) req.GetResponse ();
			Assertion.AssertEquals ("res:HttpStatusCode: ", "OK", res.StatusCode.ToString ());
			Assertion.AssertEquals ("res:HttpStatusDescription: ", "OK", res.StatusDescription);
			
			Assertion.AssertEquals ("res Header 1", "text/html", res.Headers.Get ("Content-Type"));
			Assertion.AssertNotNull ("Last Modified: ", res.LastModified);
			
			Assertion.AssertEquals ("res:", 0, res.Cookies.Count);
				
			res.Close ();
		}
		
		[Test]
		public void AddRange ()
		{
			HttpWebRequest req = (HttpWebRequest) WebRequest.Create ("http://www.google.com");
			req.AddRange (10);
			req.AddRange (50, 90);
			req.AddRange ("bytes", 100); 
			req.AddRange ("bytes", 100, 120);
			Assertion.AssertEquals ("#1", "bytes=10-,50-90,100-,100-120", req.Headers ["Range"]);
			try {
				req.AddRange ("bits", 2000);
				Assertion.Fail ("#2");
			} catch (InvalidOperationException) {}
		}

		[Test]
		[Category("InetAccess")] 
		public void Cookies1 ()
		{
			// The purpose of this test is to ensure that the cookies we get from a request
			// are stored in both, the CookieCollection in HttpWebResponse and the CookieContainer
			// in HttpWebRequest.
			// If this URL stops sending *one* and only one cookie, replace it.
			string url = "http://www.elmundo.es";
			CookieContainer cookies = new CookieContainer ();
			HttpWebRequest req = (HttpWebRequest) WebRequest.Create (url);
			req.KeepAlive = false;
			req.UserAgent = "Mozilla/5.0 (Windows; U; Windows NT 5.1; en-US; rv; 1.7.6) Gecko/20050317 Firefox/1.0.2";
			req.CookieContainer = cookies;
			Assertion.AssertEquals ("#01", 0, cookies.Count);
			using (HttpWebResponse res = (HttpWebResponse) req.GetResponse()) {
				CookieCollection coll = req.CookieContainer.GetCookies (new Uri (url));
				Assertion.AssertEquals ("#02", 1, coll.Count);
				Assertion.AssertEquals ("#03", 1, res.Cookies.Count);
				Cookie one = coll [0];
				Cookie two = res.Cookies [0];
				Assertion.AssertEquals ("#04", true, object.ReferenceEquals (one, two));
			}
		}

		[Test]
		public void SslClientBlock ()
		{
			// If there's a regression for this, the test will hang.
			ServicePointManager.CertificatePolicy = new AcceptAllPolicy ();
			try {
				SslHttpServer server = new SslHttpServer ();
				server.Start ();

				string url = String.Format ("https://{0}:{1}/nothing.html", server.IPAddress, server.Port);
				HttpWebRequest request = (HttpWebRequest) WebRequest.Create (url);
				request.Method = "POST";
				Stream stream = request.GetRequestStream ();
				byte [] bytes = new byte [100];
				stream.Write (bytes, 0, bytes.Length);
				stream.Close ();
				HttpWebResponse resp = (HttpWebResponse) request.GetResponse ();
				Assertion.AssertEquals ("StatusCode", 200, (int) resp.StatusCode);
				StreamReader sr = new StreamReader (resp.GetResponseStream (), Encoding.UTF8);
				string x = sr.ReadToEnd ();
				sr.Close ();
				resp.Close ();
				server.Stop ();
				if (server.Error != null)
					throw server.Error;
			} finally {
				ServicePointManager.CertificatePolicy = null;
			}
		}

		class AcceptAllPolicy : ICertificatePolicy {
			public bool CheckValidationResult (ServicePoint sp, X509Certificate certificate, WebRequest request, int error)
			{
				return true;
			}
		}

		class SslHttpServer
		{
			X509Certificate _certificate;
			Socket sock;
			ManualResetEvent evt;
			Exception error;

			public SslHttpServer ()
			{
				sock = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				sock.Bind (new IPEndPoint (IPAddress.Loopback, 0));
				sock.Listen (1);
			}

			public void Start ()
			{
				evt = new ManualResetEvent (false);
				Thread th = new Thread (new ThreadStart (Run));
				th.Start ();
			}

			public void Stop ()
			{
				evt.Set ();
			}
			
			public IPAddress IPAddress {
				get { return ((IPEndPoint) sock.LocalEndPoint).Address; }
			}
			
			public int Port {
				get { return ((IPEndPoint) sock.LocalEndPoint).Port; }
			}

			public Exception Error { 
				get { return error; }
			}

			void Run ()
			{
				try {
					Socket client = sock.Accept ();
					NetworkStream ns = new NetworkStream (client, true);
					SslServerStream s = new SslServerStream (ns, Certificate, false, false);
					s.PrivateKeyCertSelectionDelegate += new PrivateKeySelectionCallback (GetPrivateKey);

					StreamReader reader = new StreamReader (s);
					StreamWriter writer = new StreamWriter (s, Encoding.ASCII);

					string line;
					string hello = "<html><body><h1>Hello World!</h1></body></html>";
					string answer = "HTTP/1.0 200\r\n" +
							"Connection: close\r\n" +
							"Content-Type: text/html\r\n" +
							"Content-Encoding: " + Encoding.ASCII.WebName + "\r\n" +
							"Content-Length: " + hello.Length + "\r\n" +
							"\r\n" + hello;

					// Read the headers
					do {
						line = reader.ReadLine ();
					} while (line != "" && line != null && line.Length > 0);

					// Now the content. We know it's 100 bytes.
					// This makes BeginRead in sslclientstream block.
					char [] cs = new char [100];
					reader.Read (cs, 0, 100);

					writer.Write (answer);
					writer.Flush ();
					evt.WaitOne (50000, false);
					sock.Close ();
				} catch (Exception e) {
					error = e;
				}
			}

			X509Certificate Certificate {
				get {
					if (_certificate == null)
						_certificate = new X509Certificate (CertData.Certificate);

					return _certificate;
				}
			}

			AsymmetricAlgorithm GetPrivateKey (X509Certificate certificate, string targetHost)
			{
				PrivateKey key = new PrivateKey (CertData.PrivateKey, null);
				return key.RSA;
			}
		}

		class CertData {
			public readonly static byte [] Certificate = {
				48, 130, 1, 191, 48, 130, 1, 40, 160, 3, 2, 1, 2, 2, 16, 36, 
				14, 97, 190, 146, 132, 208, 71, 175, 6, 87, 168, 185, 175, 55, 43, 48, 
				13, 6, 9, 42, 134, 72, 134, 247, 13, 1, 1, 4, 5, 0, 48, 18, 
				49, 16, 48, 14, 6, 3, 85, 4, 3, 19, 7, 103, 111, 110, 122, 97, 
				108, 111, 48, 30, 23, 13, 48, 53, 48, 54, 50, 50, 49, 57, 51, 48, 
				52, 54, 90, 23, 13, 51, 57, 49, 50, 51, 49, 50, 51, 53, 57, 53, 
				57, 90, 48, 18, 49, 16, 48, 14, 6, 3, 85, 4, 3, 19, 7, 103, 
				111, 110, 122, 97, 108, 111, 48, 129, 158, 48, 13, 6, 9, 42, 134, 72, 
				134, 247, 13, 1, 1, 1, 5, 0, 3, 129, 140, 0, 48, 129, 136, 2, 
				129, 129, 0, 138, 9, 38, 25, 166, 252, 59, 26, 39, 184, 128, 216, 38, 
				73, 41, 86, 30, 228, 160, 205, 41, 135, 115, 223, 44, 62, 42, 198, 178, 
				190, 81, 11, 25, 21, 216, 49, 179, 130, 246, 52, 97, 175, 212, 94, 157, 
				231, 162, 66, 161, 103, 63, 204, 83, 141, 172, 119, 97, 225, 206, 98, 101, 
				210, 106, 2, 206, 81, 90, 173, 47, 41, 199, 209, 241, 177, 177, 96, 207, 
				254, 220, 190, 66, 180, 153, 0, 209, 14, 178, 69, 194, 3, 37, 116, 239, 
				49, 23, 185, 245, 255, 126, 35, 85, 246, 56, 244, 107, 117, 24, 14, 57, 
				9, 111, 147, 189, 220, 142, 57, 104, 153, 193, 205, 19, 14, 22, 157, 16, 
				24, 80, 201, 2, 2, 0, 17, 163, 23, 48, 21, 48, 19, 6, 3, 85, 
				29, 37, 4, 12, 48, 10, 6, 8, 43, 6, 1, 5, 5, 7, 3, 1, 
				48, 13, 6, 9, 42, 134, 72, 134, 247, 13, 1, 1, 4, 5, 0, 3, 
				129, 129, 0, 64, 49, 57, 253, 218, 198, 229, 51, 189, 12, 154, 225, 183, 
				160, 147, 90, 113, 172, 69, 122, 28, 77, 97, 215, 231, 194, 150, 29, 196, 
				65, 95, 218, 99, 142, 111, 79, 205, 109, 76, 32, 92, 220, 76, 88, 53, 
				237, 80, 11, 85, 44, 91, 21, 210, 12, 34, 223, 234, 18, 187, 136, 62, 
				26, 240, 103, 180, 12, 226, 221, 250, 247, 129, 51, 23, 129, 165, 56, 67, 
				43, 83, 244, 110, 207, 24, 253, 195, 16, 46, 80, 113, 80, 18, 2, 254, 
				120, 147, 151, 164, 23, 210, 230, 100, 19, 197, 179, 28, 194, 48, 106, 159, 
				155, 144, 37, 82, 44, 160, 40, 52, 146, 174, 77, 188, 160, 230, 75, 172, 
				123, 3, 254, 
			};

			public readonly static byte [] PrivateKey = {
				30, 241, 181, 176, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 
				0, 0, 0, 0, 84, 2, 0, 0, 7, 2, 0, 0, 0, 36, 0, 0, 
				82, 83, 65, 50, 0, 4, 0, 0, 17, 0, 0, 0, 201, 80, 24, 16, 
				157, 22, 14, 19, 205, 193, 153, 104, 57, 142, 220, 189, 147, 111, 9, 57, 
				14, 24, 117, 107, 244, 56, 246, 85, 35, 126, 255, 245, 185, 23, 49, 239, 
				116, 37, 3, 194, 69, 178, 14, 209, 0, 153, 180, 66, 190, 220, 254, 207, 
				96, 177, 177, 241, 209, 199, 41, 47, 173, 90, 81, 206, 2, 106, 210, 101, 
				98, 206, 225, 97, 119, 172, 141, 83, 204, 63, 103, 161, 66, 162, 231, 157, 
				94, 212, 175, 97, 52, 246, 130, 179, 49, 216, 21, 25, 11, 81, 190, 178, 
				198, 42, 62, 44, 223, 115, 135, 41, 205, 160, 228, 30, 86, 41, 73, 38, 
				216, 128, 184, 39, 26, 59, 252, 166, 25, 38, 9, 138, 175, 88, 190, 223, 
				27, 24, 224, 123, 190, 69, 164, 234, 129, 59, 108, 229, 248, 62, 187, 15, 
				235, 147, 162, 83, 47, 123, 170, 190, 224, 31, 215, 110, 143, 31, 227, 216, 
				85, 88, 154, 83, 207, 229, 41, 28, 237, 116, 181, 17, 37, 141, 224, 185, 
				164, 144, 141, 233, 164, 138, 177, 241, 115, 181, 230, 150, 7, 92, 139, 141, 
				113, 95, 57, 191, 211, 165, 217, 250, 197, 68, 164, 184, 168, 43, 48, 65, 
				177, 237, 173, 144, 148, 221, 62, 189, 147, 63, 216, 188, 206, 103, 226, 171, 
				32, 20, 230, 116, 144, 192, 1, 39, 202, 87, 74, 250, 6, 142, 188, 23, 
				45, 4, 112, 191, 253, 67, 69, 70, 128, 143, 44, 234, 41, 96, 195, 82, 
				202, 35, 158, 149, 240, 151, 23, 25, 166, 179, 85, 144, 58, 120, 149, 229, 
				205, 34, 8, 110, 86, 119, 130, 210, 37, 173, 65, 71, 169, 67, 8, 51, 
				20, 96, 51, 155, 3, 39, 85, 187, 40, 193, 57, 19, 99, 78, 173, 28, 
				129, 154, 108, 175, 8, 138, 237, 71, 27, 148, 129, 35, 47, 57, 101, 237, 
				168, 178, 227, 221, 212, 63, 124, 254, 253, 215, 183, 159, 49, 103, 74, 49, 
				67, 160, 171, 72, 194, 215, 108, 251, 178, 18, 184, 100, 211, 105, 21, 186, 
				39, 66, 218, 154, 72, 222, 90, 237, 179, 251, 51, 224, 212, 56, 251, 6, 
				209, 151, 198, 176, 89, 110, 35, 141, 248, 237, 223, 68, 135, 206, 207, 169, 
				254, 219, 243, 130, 71, 11, 94, 113, 233, 92, 63, 156, 169, 72, 215, 110, 
				95, 94, 191, 50, 59, 89, 187, 59, 183, 99, 161, 146, 233, 245, 219, 80, 
				87, 113, 251, 50, 144, 195, 158, 46, 189, 232, 119, 91, 75, 22, 6, 176, 
				39, 206, 25, 196, 213, 195, 219, 24, 28, 103, 104, 36, 137, 128, 4, 119, 
				163, 40, 126, 87, 18, 86, 128, 243, 213, 101, 2, 237, 78, 64, 160, 55, 
				199, 93, 90, 126, 175, 199, 55, 89, 234, 190, 5, 16, 196, 88, 28, 208, 
				28, 92, 32, 115, 204, 9, 202, 101, 15, 123, 43, 75, 90, 144, 95, 179, 
				102, 249, 57, 150, 204, 99, 147, 203, 16, 63, 81, 244, 226, 237, 82, 204, 
				20, 200, 140, 65, 83, 217, 161, 23, 123, 37, 115, 12, 100, 73, 70, 190, 
				32, 235, 174, 140, 148, 157, 47, 238, 40, 208, 228, 80, 54, 187, 156, 252, 
				253, 230, 231, 156, 138, 125, 96, 79, 3, 27, 143, 55, 146, 169, 165, 61, 
				238, 60, 227, 77, 217, 93, 117, 122, 111, 46, 173, 113, 
			};
		}
	}
}

