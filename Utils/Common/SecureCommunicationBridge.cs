using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VPetLLM.Utils.Common
{
    /// <summary>
	/// Private native communication adapter. Identity collection, key generation,
	/// cryptography, and the binary protocol live in the private native module.
    /// </summary>
    internal static class SecureCommunicationBridge
    {
        private const string NativeLibraryName = "VPetLLM.SecureCommunication";
		private const int PacketCapacity = 4096;
		private const int ProtocolPacketCapacity = 1024;
		private const int ChunkPlaintextBytes = 64 * 1024;
		private const int ChunkTagBytes = 16;
		private const int ChunkHeaderBytes = 5;
		private const byte FinalChunkFlag = 1;
		private const string TransportVersion = "2";
		private const string HeaderTransport = "X-VPet-Transport";
		private const string HeaderWrappedKey = "X-VPet-Wrapped-Key";
		private const string HeaderRequestNonce = "X-VPet-Request-Nonce";
		private const string HeaderResponseNonce = "X-VPet-Response-Nonce";
		private const string HeaderRequestId = "X-VPet-Request-Id";
		private const string HeaderOriginalContentType = "X-VPet-Original-Content-Type";
        private static readonly TimeSpan CheckKeyWaitTimeout = TimeSpan.FromSeconds(35);
        private static readonly TimeSpan CheckKeyPollInterval = TimeSpan.FromMilliseconds(50);

        static SecureCommunicationBridge()
        {
            NativeLibrary.SetDllImportResolver(typeof(SecureCommunicationBridge).Assembly, ResolveNativeLibrary);
        }

        internal static async Task AddSignatureAsync(HttpRequestMessage request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var packet = new byte[PacketCapacity];
            try
            {
                var deadline = DateTime.UtcNow + CheckKeyWaitTimeout;
                int written;
                while ((written = NativeMethods.CreateHeaderPacket(packet, packet.Length)) == -13)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("VPet authorization key was not returned in time.");
                    await Task.Delay(CheckKeyPollInterval).ConfigureAwait(false);
                }

                if (written <= 0)
                    throw new InvalidOperationException($"Secure communication payload failed ({written}).");

                ApplyHeaderPacket(request, packet.AsSpan(0, written));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packet);
            }
        }

		internal static async Task<HttpResponseMessage> SendAsync(
			HttpClient client,
			HttpRequestMessage request,
			HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
			CancellationToken cancellationToken = default)
        {
			ArgumentNullException.ThrowIfNull(client);
			ArgumentNullException.ThrowIfNull(request);
			await AddSignatureAsync(request).ConfigureAwait(false);

			ProtectedRequestLease? protectedRequest = null;
			var responseOwnsLease = false;
            try
            {
				protectedRequest = await ProtectRequestAsync(request, cancellationToken).ConfigureAwait(false);
				var response = await client.SendAsync(
					request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
					.ConfigureAwait(false);
				try
				{
					responseOwnsLease = await UnprotectResponseAsync(
						response, protectedRequest, cancellationToken)
						.ConfigureAwait(false);
					return response;
				}
				catch
				{
					response.Dispose();
					throw;
				}
            }
            finally
            {
				if (!responseOwnsLease)
					protectedRequest?.Dispose();
            }
        }

		private static Task<ProtectedRequestLease> ProtectRequestAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var originalContent = request.Content;
			var originalContentType = GetRawContentType(originalContent);
			var contentTypeBytes = Encoding.UTF8.GetBytes(originalContentType);
			var packet = new byte[ProtocolPacketCapacity];
			ProtectedRequestLease? lease = null;
			try
			{
				var written = NativeMethods.BeginChunkedRequest(
					contentTypeBytes, contentTypeBytes.Length, packet, packet.Length);
				if (written <= 0)
					throw new InvalidOperationException($"Secure request initialization failed ({written}).");
				var protectedRequest = ParseProtectedRequest(packet.AsSpan(0, written));
				lease = new ProtectedRequestLease(protectedRequest.RequestId);

				request.Content = new ChunkedAeadHttpContent(originalContent, lease);
				request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/vnd.vpetllm.encrypted");
				SetHeader(request, HeaderTransport, TransportVersion);
				SetHeader(request, HeaderWrappedKey, ToRawBase64(packet.AsSpan(
					protectedRequest.WrappedKeyOffset, protectedRequest.WrappedKeyLength)));
				SetHeader(request, HeaderRequestNonce, ToRawBase64(packet.AsSpan(
					protectedRequest.NonceOffset, protectedRequest.NonceLength)));
				SetHeader(request, HeaderRequestId, ToRawBase64(protectedRequest.RequestId));
				SetHeader(request, HeaderOriginalContentType,
					ToRawBase64(contentTypeBytes));
				return Task.FromResult(lease);
			}
			catch
			{
				lease?.Dispose();
				throw;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(contentTypeBytes);
				CryptographicOperations.ZeroMemory(packet);
			}
		}

		private static async Task<bool> UnprotectResponseAsync(
			HttpResponseMessage response,
			ProtectedRequestLease lease,
			CancellationToken cancellationToken)
		{
			if (!response.Headers.TryGetValues(HeaderTransport, out var versions) ||
				!versions.Contains(TransportVersion, StringComparer.Ordinal))
				throw new HttpRequestException("The authentication service returned an unauthenticated response.");
			if (!response.Headers.TryGetValues(HeaderResponseNonce, out var nonceValues))
				throw new HttpRequestException("The authentication service response is missing its nonce.");

			byte[] nonce;
			try { nonce = FromRawBase64(nonceValues.Single()); }
			catch (Exception ex) when (ex is FormatException or InvalidOperationException)
			{
				throw new HttpRequestException("The authentication service response nonce is invalid.", ex);
			}
			if (nonce.Length != 12)
				throw new HttpRequestException("The authentication service response nonce has an invalid length.");

			var contentType = GetRawContentType(response.Content);
			var contentTypeBytes = Encoding.UTF8.GetBytes(contentType);
			try
			{
				var oldContent = response.Content;
				var source = await oldContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				var plaintextStream = new ChunkedAeadReadStream(
					oldContent, source, lease, (int)response.StatusCode, contentTypeBytes, nonce);
				var replacement = new StreamContent(plaintextStream);
				foreach (var header in oldContent.Headers)
				{
					if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
						replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
				response.Content = replacement;
				return true;
			}
			finally
			{
				CryptographicOperations.ZeroMemory(nonce);
				CryptographicOperations.ZeroMemory(contentTypeBytes);
			}
		}

		private static ProtectedRequest ParseProtectedRequest(ReadOnlySpan<byte> packet)
		{
			const int fixedLength = 1 + 16 + 12 + 2;
			if (packet.Length < fixedLength || packet[0] != 2)
				throw new InvalidOperationException("Secure request packet is invalid.");
			var offset = 1;
			var requestId = packet.Slice(offset, 16).ToArray();
			offset += 16;
			var nonceOffset = offset;
			const int nonceLength = 12;
			offset += 12;
			var wrappedLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset, 2));
			offset += 2;
			if (wrappedLength == 0 || (long)offset + wrappedLength != packet.Length)
				throw new InvalidOperationException("Secure request packet is malformed.");
			var wrappedKeyOffset = offset;
			return new ProtectedRequest(
				requestId,
				nonceOffset,
				nonceLength,
				wrappedKeyOffset,
				wrappedLength);
		}

		private sealed record ProtectedRequest(
			byte[] RequestId,
			int NonceOffset,
			int NonceLength,
			int WrappedKeyOffset,
			int WrappedKeyLength);

		private sealed class ProtectedRequestLease : IDisposable
		{
			internal ProtectedRequestLease(byte[] requestId)
			{
				RequestId = requestId;
			}

			internal byte[] RequestId { get; }
			private int _disposed;

			public void Dispose()
			{
				if (Interlocked.Exchange(ref _disposed, 1) != 0)
					return;
				NativeMethods.ForgetProtocolSession(RequestId, RequestId.Length);
				CryptographicOperations.ZeroMemory(RequestId);
			}
		}

		private sealed class ChunkedAeadHttpContent : HttpContent
		{
			private readonly HttpContent? _source;
			private readonly ProtectedRequestLease _lease;
			private int _serialized;

			internal ChunkedAeadHttpContent(HttpContent? source, ProtectedRequestLease lease)
			{
				_source = source;
				_lease = lease;
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
				=> SerializeToStreamAsync(stream, context, CancellationToken.None);

			protected override async Task SerializeToStreamAsync(
				Stream stream, TransportContext? context, CancellationToken cancellationToken)
			{
				if (Interlocked.Exchange(ref _serialized, 1) != 0)
					throw new InvalidOperationException("An encrypted request cannot be replayed.");

				var plaintext = ArrayPool<byte>.Shared.Rent(ChunkPlaintextBytes);
				var encrypted = ArrayPool<byte>.Shared.Rent(ChunkPlaintextBytes + ChunkTagBytes);
				try
				{
					uint index = 0;
					if (_source is not null)
					{
						await using var sourceStream = await _source.ReadAsStreamAsync(cancellationToken)
							.ConfigureAwait(false);
						while (true)
						{
							var read = await sourceStream.ReadAsync(
								plaintext.AsMemory(0, ChunkPlaintextBytes), cancellationToken)
								.ConfigureAwait(false);
							if (read == 0)
								break;
							var written = NativeMethods.EncryptRequestChunk(
								_lease.RequestId, _lease.RequestId.Length, index, 0,
								plaintext, read, encrypted, encrypted.Length);
							if (written != read + ChunkTagBytes)
								throw new HttpRequestException($"Secure request chunk protection failed ({written}).");
							await WriteFrameAsync(stream, 0, encrypted, written, cancellationToken)
								.ConfigureAwait(false);
							CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, read));
							CryptographicOperations.ZeroMemory(encrypted.AsSpan(0, written));
							index++;
						}
					}

					var finalLength = NativeMethods.EncryptRequestChunk(
						_lease.RequestId, _lease.RequestId.Length, index, FinalChunkFlag,
						Array.Empty<byte>(), 0, encrypted, encrypted.Length);
					if (finalLength != ChunkTagBytes)
						throw new HttpRequestException($"Secure request finalization failed ({finalLength}).");
					await WriteFrameAsync(stream, FinalChunkFlag, encrypted, finalLength, cancellationToken)
						.ConfigureAwait(false);
				}
				finally
				{
					CryptographicOperations.ZeroMemory(plaintext);
					CryptographicOperations.ZeroMemory(encrypted);
					ArrayPool<byte>.Shared.Return(plaintext);
					ArrayPool<byte>.Shared.Return(encrypted);
				}
			}

			protected override bool TryComputeLength(out long length)
			{
				length = 0;
				return false;
			}

			protected override void Dispose(bool disposing)
			{
				if (disposing)
					_source?.Dispose();
				base.Dispose(disposing);
			}
		}

		private sealed class ChunkedAeadReadStream : Stream
		{
			private readonly HttpContent _owner;
			private readonly Stream _source;
			private readonly ProtectedRequestLease _lease;
			private readonly int _status;
			private readonly byte[] _contentType;
			private readonly byte[] _nonce;
			private readonly byte[] _encrypted = new byte[ChunkPlaintextBytes + ChunkTagBytes];
			private readonly byte[] _plaintext = new byte[ChunkPlaintextBytes];
			private uint _index;
			private int _plaintextOffset;
			private int _plaintextLength;
			private bool _final;
			private bool _disposed;

			internal ChunkedAeadReadStream(HttpContent owner, Stream source,
				ProtectedRequestLease lease, int status, byte[] contentType, byte[] nonce)
			{
				_owner = owner;
				_source = source;
				_lease = lease;
				_status = status;
				_contentType = contentType.ToArray();
				_nonce = nonce.ToArray();
			}

			public override bool CanRead => !_disposed;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

			public override int Read(byte[] buffer, int offset, int count)
				=> ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

			public override async ValueTask<int> ReadAsync(
				Memory<byte> destination, CancellationToken cancellationToken = default)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (destination.Length == 0)
					return 0;
				try
				{
					while (_plaintextOffset == _plaintextLength)
					{
						if (_final)
							return 0;
						await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
					}
					var copied = Math.Min(destination.Length, _plaintextLength - _plaintextOffset);
					_plaintext.AsMemory(_plaintextOffset, copied).CopyTo(destination);
					CryptographicOperations.ZeroMemory(_plaintext.AsSpan(_plaintextOffset, copied));
					_plaintextOffset += copied;
					return copied;
				}
				catch
				{
					Dispose();
					throw;
				}
			}

			private async Task ReadFrameAsync(CancellationToken cancellationToken)
			{
				var header = new byte[ChunkHeaderBytes];
				if (!await SecureCommunicationBridge.ReadExactlyAsync(
					_source, header, cancellationToken).ConfigureAwait(false))
					throw new HttpRequestException("The encrypted response ended without an authenticated final frame.");
				var flags = header[0];
				if (flags != 0 && flags != FinalChunkFlag)
					throw new HttpRequestException("The encrypted response frame has invalid flags.");
				var encryptedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1)));
				if (encryptedLength < ChunkTagBytes || encryptedLength > _encrypted.Length ||
					(flags == 0 && encryptedLength == ChunkTagBytes) ||
					(flags == FinalChunkFlag && encryptedLength != ChunkTagBytes))
					throw new HttpRequestException("The encrypted response frame has an invalid length.");
				if (!await SecureCommunicationBridge.ReadExactlyAsync(
					_source, _encrypted.AsMemory(0, encryptedLength), cancellationToken).ConfigureAwait(false))
					throw new HttpRequestException("The encrypted response frame is truncated.");

				var written = NativeMethods.DecryptResponseChunk(
					_lease.RequestId, _lease.RequestId.Length, _index, flags, _status,
					_contentType, _contentType.Length, _nonce, _nonce.Length,
					_encrypted, encryptedLength, _plaintext, _plaintext.Length);
				CryptographicOperations.ZeroMemory(_encrypted.AsSpan(0, encryptedLength));
				if (written < 0 || written != encryptedLength - ChunkTagBytes)
					throw new HttpRequestException($"Secure response chunk authentication failed ({written}).");
				_index++;
				if (flags == FinalChunkFlag)
				{
					var trailing = new byte[1];
					if (await _source.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
						throw new HttpRequestException("The encrypted response contains data after its final frame.");
					_final = true;
					_lease.Dispose();
					return;
				}
				_plaintextOffset = 0;
				_plaintextLength = written;
			}

			protected override void Dispose(bool disposing)
			{
				if (_disposed)
					return;
				_disposed = true;
				CryptographicOperations.ZeroMemory(_encrypted);
				CryptographicOperations.ZeroMemory(_plaintext);
				CryptographicOperations.ZeroMemory(_contentType);
				CryptographicOperations.ZeroMemory(_nonce);
				_lease.Dispose();
				if (disposing)
				{
					_source.Dispose();
					_owner.Dispose();
				}
				base.Dispose(disposing);
			}

			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}

		private static async Task WriteFrameAsync(Stream destination, byte flags,
			byte[] encrypted, int encryptedLength, CancellationToken cancellationToken)
		{
			var header = new byte[ChunkHeaderBytes];
			header[0] = flags;
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), checked((uint)encryptedLength));
			await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
			await destination.WriteAsync(encrypted.AsMemory(0, encryptedLength), cancellationToken)
				.ConfigureAwait(false);
		}

		private static async Task<bool> ReadExactlyAsync(
			Stream source, Memory<byte> destination, CancellationToken cancellationToken)
		{
			var offset = 0;
			while (offset < destination.Length)
			{
				var read = await source.ReadAsync(destination[offset..], cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					return false;
				offset += read;
			}
			return true;
		}

		private static string GetRawContentType(HttpContent? content)
		{
			if (content is not null && content.Headers.TryGetValues("Content-Type", out var values))
				return values.FirstOrDefault() ?? "";
			return "";
		}

		private static string ToRawBase64(ReadOnlySpan<byte> value)
			=> Convert.ToBase64String(value).TrimEnd('=');

		private static byte[] FromRawBase64(string value)
		{
			var padding = (4 - value.Length % 4) % 4;
			return Convert.FromBase64String(value + new string('=', padding));
		}

		private static void SetHeader(HttpRequestMessage request, string name, string value)
		{
			request.Headers.Remove(name);
			if (!request.Headers.TryAddWithoutValidation(name, value))
				throw new InvalidOperationException($"Secure communication header {name} could not be attached.");
		}

        private static void ApplyHeaderPacket(HttpRequestMessage request, ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 2 || packet[0] != 2)
                throw new InvalidOperationException("Secure communication payload returned an invalid packet.");

            var count = packet[1];
            var offset = 2;
            for (var index = 0; index < count; index++)
            {
                if (packet.Length - offset < 4)
                    throw new InvalidOperationException("Secure communication header packet is truncated.");

                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset, 2));
                var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset + 2, 2));
                offset += 4;
                if (nameLength == 0 || packet.Length - offset < nameLength + valueLength)
                    throw new InvalidOperationException("Secure communication header packet is malformed.");

                var name = Encoding.UTF8.GetString(packet.Slice(offset, nameLength));
                offset += nameLength;
                var value = Encoding.UTF8.GetString(packet.Slice(offset, valueLength));
                offset += valueLength;

                request.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    throw new InvalidOperationException("Secure communication header could not be attached.");
            }

            if (count != 7 || offset != packet.Length)
                throw new InvalidOperationException("Secure communication header packet has an unexpected shape.");
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, NativeLibraryName, StringComparison.Ordinal))
                return IntPtr.Zero;

            var assemblyDirectory = Path.GetDirectoryName(assembly.Location)
                ?? throw new DllNotFoundException("Plugin assembly location is unavailable.");
            var runtime = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var candidate = Path.Combine(
                assemblyDirectory, "runtimes", runtime, "native", NativeLibraryName + ".dll");
            if (!File.Exists(candidate))
                throw new DllNotFoundException($"Secure communication payload is missing for {runtime}.");
            return NativeLibrary.Load(candidate);
        }

		private static class NativeMethods
        {
            [DllImport(NativeLibraryName, EntryPoint = "vp_sc_v2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int CreateHeaderPacket(
                [Out] byte[] output,
                int outputCapacity);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_begin_chunked_request", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int BeginChunkedRequest(
				[In] byte[] contentType,
				int contentTypeLength,
				[Out] byte[] output,
				int outputCapacity);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_encrypt_request_chunk", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int EncryptRequestChunk(
				[In] byte[] requestId,
				int requestIdLength,
				uint index,
				byte flags,
				[In] byte[] plaintext,
				int plaintextLength,
				[Out] byte[] output,
				int outputCapacity);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_decrypt_response_chunk", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int DecryptResponseChunk(
				[In] byte[] requestId,
				int requestIdLength,
				uint index,
				byte flags,
				int status,
				[In] byte[] contentType,
				int contentTypeLength,
				[In] byte[] nonce,
				int nonceLength,
				[In] byte[] encrypted,
				int encryptedLength,
				[Out] byte[] output,
				int outputCapacity);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_forget_protocol_session", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int ForgetProtocolSession(
				[In] byte[] requestId,
				int requestIdLength);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_protect_request", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int ProtectRequest(
				[In] byte[] plaintext,
				int plaintextLength,
				[In] byte[] contentType,
				int contentTypeLength,
				[Out] byte[] output,
				int outputCapacity);

			[DllImport(NativeLibraryName, EntryPoint = "vp_sc_unprotect_response", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
			internal static extern int UnprotectResponse(
				[In] byte[] requestId,
				int requestIdLength,
				int status,
				[In] byte[] contentType,
				int contentTypeLength,
				[In] byte[] nonce,
				int nonceLength,
				[In] byte[] encrypted,
				int encryptedLength,
				[Out] byte[] output,
				int outputCapacity);
        }
    }
}
