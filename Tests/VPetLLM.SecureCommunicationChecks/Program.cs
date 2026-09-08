using System.Reflection;
using System.Runtime.CompilerServices;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

var pluginAssembly = typeof(global::VPetLLM.VPetLLM).Assembly;
var bridge = pluginAssembly.GetType("VPetLLM.Utils.Common.SecureCommunicationBridge");
Check(bridge is not null, "Secure communication bridge must exist.");
Check(bridge?.IsNotPublic == true, "The native protocol bridge must remain internal to VPetLLM.");
var pluginServiceSender = typeof(global::VPetLLM.VPetLLM).GetMethod(
    "SendAuthenticatedServiceRequestAsync",
    BindingFlags.Instance | BindingFlags.Public);
Check(pluginServiceSender?.GetParameters().Length == 3,
    "Official plugins must have one host-owned authenticated service entry point.");
Check(pluginAssembly.GetType("VPetLLM.Utils.Network.PinnedServerTrust") is null,
    "TLS/SPKI pinning must not be used; EdgeOne keeps its ordinary public certificate.");

if (bridge is not null)
{
    var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    RuntimeHelpers.RunClassConstructor(bridge.TypeHandle);
    var addSignature = bridge.GetMethod("AddSignatureAsync", flags);
    var send = bridge.GetMethod("SendAsync", flags);
	var protectRequest = bridge.GetMethod("ProtectRequestAsync", flags);
    var nativeMethods = bridge.GetNestedType("NativeMethods", BindingFlags.NonPublic);
    var nativeCreate = nativeMethods?.GetMethod("CreateHeaderPacket", flags);
    var nativeBeginChunked = nativeMethods?.GetMethod("BeginChunkedRequest", flags);
    var nativeEncryptChunk = nativeMethods?.GetMethod("EncryptRequestChunk", flags);
    var nativeDecryptChunk = nativeMethods?.GetMethod("DecryptResponseChunk", flags);
    var nativeForget = nativeMethods?.GetMethod("ForgetProtocolSession", flags);
    var nativeProtect = nativeMethods?.GetMethod("ProtectRequest", flags);
    var nativeUnprotect = nativeMethods?.GetMethod("UnprotectResponse", flags);

    Check(bridge.GetMethod("Init", flags) is null,
        "Managed code must not expose identity-provider initialization.");
    Check(addSignature is not null, "Secure identity header operation must be available.");
    Check(send is not null, "Authenticated application transport operation must be available.");
    Check(nativeCreate?.GetParameters().Length == 2,
        "Native identity operation must accept only output buffer and capacity.");
    Check(nativeBeginChunked?.GetParameters().Length == 4,
        "Native DLL must create protocol-v2 sessions without exposing their AES key.");
    Check(nativeEncryptChunk?.GetParameters().Length == 8,
        "Native DLL must encrypt ordered request chunks.");
    Check(nativeDecryptChunk?.GetParameters().Length == 13,
        "Native DLL must authenticate ordered response chunks.");
    Check(nativeForget?.GetParameters().Length == 2,
        "Native DLL must support explicit abandoned-session cleanup.");
    Check(nativeProtect?.GetParameters().Length == 6,
        "Native DLL must own request encryption and protocol-public-key wrapping.");
    Check(nativeUnprotect?.GetParameters().Length == 11,
        "Native DLL must own authenticated response decryption.");
	Check(protectRequest?.GetParameters().Length == 2,
		"Managed request framing must pass only the request and cancellation token.");

    if (nativeProtect is not null)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes("private request");
        var contentType = System.Text.Encoding.ASCII.GetBytes("application/json");
        var first = new byte[2048];
        var second = new byte[2048];
        var firstLength = (int)nativeProtect.Invoke(null,
            [plaintext, plaintext.Length, contentType, contentType.Length, first, first.Length])!;
        var secondLength = (int)nativeProtect.Invoke(null,
            [plaintext, plaintext.Length, contentType, contentType.Length, second, second.Length])!;
        Check(firstLength > plaintext.Length && secondLength > plaintext.Length,
            "Native request protector must return an authenticated encrypted envelope.");
        Check(!first.AsSpan(0, Math.Max(0, firstLength)).SequenceEqual(
                second.AsSpan(0, Math.Max(0, secondLength))),
            "Every protected request must use fresh session and anti-replay material.");
    }

	if (protectRequest is not null)
	{
		const string secret = "managed framing private request";
		using var protectedRequest = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/")
		{
			Content = new StringContent(secret, System.Text.Encoding.UTF8, "application/json")
		};
		var task = (Task)protectRequest.Invoke(null, [protectedRequest, CancellationToken.None])!;
		await task;
		var lease = task.GetType().GetProperty("Result")!.GetValue(task)!;
		try
		{
			var requestId = (byte[])lease.GetType().GetProperty(
				"RequestId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
			var encrypted = await protectedRequest.Content.ReadAsByteArrayAsync();
			Check(requestId.Length == 16, "Native request protector must return a 128-bit request identifier.");
			Check(protectedRequest.Headers.Contains("X-VPet-Wrapped-Key") &&
				protectedRequest.Headers.Contains("X-VPet-Request-Nonce"),
				"Managed framing must attach the opaque native envelope.");
			Check(protectedRequest.Headers.GetValues("X-VPet-Transport").Single() == "2",
				"Managed framing must use chunked AEAD protocol v2.");
			Check(encrypted.Length >= 5 + 16 && encrypted[0] == 0 &&
				encrypted[^21] == 1,
				"Managed framing must include data and authenticated final frames.");
			Check(!System.Text.Encoding.UTF8.GetString(encrypted).Contains(secret, StringComparison.Ordinal),
				"The HTTP request body must not contain the original plaintext.");
		}
		finally
		{
			((IDisposable)lease).Dispose();
		}
	}

    if (addSignature is not null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/");
        try
        {
            await (Task)addSignature.Invoke(null, [request])!;
            Check(request.Headers.Count() == 7,
                "Native identity payload must attach exactly seven authentication headers.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException?.Message.Contains("(-10)") == true)
        {
            Console.WriteLine("Steam API is not loaded in the standalone check process; interface shape verified.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(-10)"))
        {
            Console.WriteLine("Steam API is not loaded in the standalone check process; interface shape verified.");
        }
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine("Secure application transport checks passed.");
return 0;
