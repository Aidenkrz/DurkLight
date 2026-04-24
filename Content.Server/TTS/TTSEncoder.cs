using System.IO;
using OggVorbisEncoder;

namespace Content.Server.TTS;

/// <summary>
/// Encodes raw 16-bit mono PCM audio from the Piper TTS server into OGG Vorbis
/// so it can be sent to clients at a fraction of the size. Clients decode via
/// <c>IAudioManager.LoadAudioOggVorbis</c>, which is sandbox-whitelisted.
/// </summary>
// ReSharper disable once InconsistentNaming
public static class TTSEncoder
{
    public const int SampleRate = 22050;
    private const int Channels = 1;
    private const float Quality = 0.3f; // ~40 kbps VBR for speech

    private const int WriteBufferLength = 1024;

    public static byte[] EncodePcmToOggVorbis(ReadOnlySpan<byte> input)
    {
        var pcm = StripWavHeader(input);
        var sampleCount = pcm.Length / 2;

        var samples = new float[Channels][];
        samples[0] = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var s = (short) ((pcm[i * 2 + 1] << 8) | (pcm[i * 2] & 0xFF));
            samples[0][i] = s / 32768f;
        }

        var info = VorbisInfo.InitVariableBitRate(Channels, SampleRate, Quality);
        var serial = Random.Shared.Next();
        var oggStream = new OggStream(serial);

        using var output = new MemoryStream();

        var comments = new Comments();
        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

        FlushPages(oggStream, output, flushAll: true);

        var processingState = ProcessingState.Create(info);

        var offset = 0;
        while (offset < sampleCount)
        {
            var chunk = Math.Min(WriteBufferLength, sampleCount - offset);
            var buffer = new float[Channels][];
            buffer[0] = new float[chunk];
            Array.Copy(samples[0], offset, buffer[0], 0, chunk);
            processingState.WriteData(buffer, chunk);
            offset += chunk;

            DrainPackets(processingState, oggStream);
            FlushPages(oggStream, output, flushAll: false);
        }

        processingState.WriteEndOfStream();
        DrainPackets(processingState, oggStream);
        FlushPages(oggStream, output, flushAll: true);

        return output.ToArray();
    }

    private static void DrainPackets(ProcessingState state, OggStream oggStream)
    {
        while (!oggStream.Finished && state.PacketOut(out var packet))
            oggStream.PacketIn(packet);
    }

    private static void FlushPages(OggStream oggStream, Stream output, bool flushAll)
    {
        while (oggStream.PageOut(out var page, flushAll))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }

    private static ReadOnlySpan<byte> StripWavHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44)
            return data;
        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
            return data;
        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
            return data;

        // Scan RIFF chunks for 'data'; return payload.
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id0 = data[pos];
            var id1 = data[pos + 1];
            var id2 = data[pos + 2];
            var id3 = data[pos + 3];
            var size = data[pos + 4] | (data[pos + 5] << 8) | (data[pos + 6] << 16) | (data[pos + 7] << 24);
            pos += 8;
            if (id0 == 'd' && id1 == 'a' && id2 == 't' && id3 == 'a')
            {
                var end = Math.Min(pos + size, data.Length);
                return data[pos..end];
            }
            pos += size;
        }
        return data;
    }
}
