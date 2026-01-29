using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Extensions;

[ExcludeFromCodeCoverage]
public static class StreamExtensions
{
    public static string CalcularSha256(this Stream conteudo)
    {
        var sha256 = SHA256.Create();

        byte[] hashBytes = sha256.ComputeHash(conteudo);
        string hashString = BitConverter.ToString(hashBytes).Replace("-", String.Empty);
        return hashString;
    }

    public static async Task<string> ReadStreamInChunksAsync(
        this Stream stream,
        int bufferLegth = 4096,
        bool resetStreamPosition = false)
    {
        stream.Seek(0, SeekOrigin.Begin);
        string result;
        using (var textWriter = new StringWriter())
        using (var reader = new StreamReader(stream))
        {
            var readChunk = new char[bufferLegth];
            int readChunkLength;
            //do while: is useful for the last iteration in case readChunkLength < chunkLength
            do
            {
                readChunkLength = await reader.ReadBlockAsync(readChunk, 0, bufferLegth);
                await textWriter.WriteAsync(readChunk, 0, readChunkLength);
            } while (readChunkLength > 0);

            result = textWriter.ToString();
        }

        if (resetStreamPosition)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        return result;
    }
}
