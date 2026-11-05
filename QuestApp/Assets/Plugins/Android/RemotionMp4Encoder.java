package com.remotionxr.encoder;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Minimal H.264 MP4 writer for Quest camera frames.
 *
 * Smoothness contract: Unity's render thread only hands over an RGBA byte
 * array (already copied by JNI) into a small bounded queue and returns.
 * Color conversion, MediaCodec and MediaMuxer all run on a dedicated
 * encoder thread, so recording can never stall rendering. If the encoder
 * falls behind, frames are dropped instead of freezing the app.
 */
public class RemotionMp4Encoder {
    private static final String TAG = "RemotionMp4Encoder";
    private static final String MIME = "video/avc";
    private static final Object LOCK = new Object();

    private static MediaCodec codec;
    private static MediaMuxer muxer;
    private static int trackIndex = -1;
    private static boolean muxerStarted = false;
    private static long ptsUs = 0;
    private static int frameRate = 8;
    private static int frameWidth = 0;
    private static int frameHeight = 0;
    private static final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    private static final class Frame { final byte[] rgba; final long ptsUs; Frame(byte[] r, long p) { rgba = r; ptsUs = p; } }
    private static ArrayBlockingQueue<Frame> queue;
    private static Thread worker;
    private static volatile boolean running = false;
    private static volatile boolean finalized = false;
    private static byte[] nv12Buffer;
    // Encoder input buffers are usually stride/slice-height padded
    // (e.g. 512x288 for a 480x270 frame). Writing tightly packed NV12
    // into a padded buffer tears every row and leaves the tail zeroed
    // (decoded as bright green) — the garbled original videos.
    private static int stride = 0;
    private static int sliceHeight = 0;
    private static boolean planar = false;

    public static boolean start(String path, int width, int height, int fps) {
        synchronized (LOCK) {
            try {
                stopLocked();
                finalized = false;
                frameWidth = width;
                frameHeight = height;
                frameRate = Math.max(1, fps);
                MediaFormat format = MediaFormat.createVideoFormat(MIME, width, height);
                format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                        MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
                format.setInteger(MediaFormat.KEY_BIT_RATE, 1_200_000);
                format.setInteger(MediaFormat.KEY_FRAME_RATE, frameRate);
                format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
                codec = MediaCodec.createEncoderByType(MIME);
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                codec.start();
                MediaFormat inputFormat = codec.getInputFormat();
                stride = inputFormat.containsKey("stride")
                        ? inputFormat.getInteger("stride") : width;
                sliceHeight = inputFormat.containsKey("slice-height")
                        ? inputFormat.getInteger("slice-height") : height;
                if (stride < width) stride = width;
                if (sliceHeight < height) sliceHeight = height;
                int colorFormat = inputFormat.containsKey(MediaFormat.KEY_COLOR_FORMAT)
                        ? inputFormat.getInteger(MediaFormat.KEY_COLOR_FORMAT) : 0;
                planar = colorFormat == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar;
                Log.i(TAG, "encoder input stride=" + stride + " sliceHeight=" + sliceHeight
                        + " colorFormat=0x" + Integer.toHexString(colorFormat) + " planar=" + planar);
                muxer = new MediaMuxer(path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
                trackIndex = -1;
                muxerStarted = false;
                ptsUs = 0;
                nv12Buffer = new byte[stride * sliceHeight * 3 / 2];
                queue = new ArrayBlockingQueue<>(4);
                running = true;
                worker = new Thread(RemotionMp4Encoder::encodeLoop, "RemotionMp4Encoder");
                worker.start();
                return true;
            } catch (IOException | RuntimeException e) {
                Log.e(TAG, "start failed", e);
                stopLocked();
                return false;
            }
        }
    }

    /** Non-blocking: drops the frame rather than ever stalling Unity. */
    public static void addFrame(byte[] rgba, int width, int height, long ptsUs) {
        ArrayBlockingQueue<Frame> q = queue;
        if (!running || q == null || rgba == null) return;
        if (width != frameWidth || height != frameHeight) return;
        q.offer(new Frame(rgba, ptsUs));
    }

    public static boolean finish() {
        synchronized (LOCK) {
            stopLocked();
            return finalized;
        }
    }

    private static void stopLocked() {
        running = false;
        if (worker != null) {
            try { worker.join(10_000); } catch (InterruptedException ignored) {}
            if (worker.isAlive()) {
                // Codec ownership stays with the worker until it ends;
                // releasing here would be use-after-release.
                throw new IllegalStateException("Encoder did not finish within 10 seconds");
            }
            worker = null;
        }
        // Only startup failures have no worker to release these resources.
        releaseResources(false);
    }

    private static void releaseResources(boolean success) {
        try { if (codec != null) codec.stop(); } catch (RuntimeException e) { success = false; }
        try { if (codec != null) codec.release(); } catch (RuntimeException ignored) {}
        if (muxer != null) {
            try {
                if (muxerStarted) { muxer.stop(); finalized = success; }
            } catch (RuntimeException e) { finalized = false; Log.e(TAG, "MP4 finalization failed", e); }
            finally { try { muxer.release(); } catch (RuntimeException ignored) {} }
        }
        codec = null;
        muxer = null;
        muxerStarted = false;
        trackIndex = -1;
        queue = null;
    }

    private static void encodeLoop() {
        boolean success = false;
        try {
            while (running || (queue != null && !queue.isEmpty())) {
                Frame frame = queue.poll(100, TimeUnit.MILLISECONDS);
                if (frame == null) continue;
                encodeOne(frame);
            }
            // Drain output while waiting for an input buffer, then send EOS.
            long deadline = android.os.SystemClock.elapsedRealtime() + 3000;
            int inputIndex;
            while ((inputIndex = codec.dequeueInputBuffer(100_000)) < 0) {
                drain(false);
                if (android.os.SystemClock.elapsedRealtime() > deadline)
                    throw new IllegalStateException("No encoder buffer for EOS");
            }
            codec.queueInputBuffer(inputIndex, 0, 0, ptsUs + 1,
                    MediaCodec.BUFFER_FLAG_END_OF_STREAM);
            drain(true);
            success = true;
        } catch (Exception e) {
            Log.e(TAG, "encode loop failed", e);
        } finally {
            running = false;
            releaseResources(success);
            Log.i(TAG, "MP4 finalized=" + finalized);
        }
    }

    private static void encodeOne(Frame frame) {
        rgbaToNv12(frame.rgba, frameWidth, frameHeight, nv12Buffer);
        int inputIndex;
        try {
            inputIndex = codec.dequeueInputBuffer(100_000);
        } catch (RuntimeException e) {
            Log.e(TAG, "dequeueInputBuffer failed", e);
            return;
        }
        if (inputIndex < 0) return;
        ByteBuffer input = codec.getInputBuffer(inputIndex);
        if (input == null) return;
        input.clear();
        int length = Math.min(nv12Buffer.length, input.capacity());
        input.put(nv12Buffer, 0, length);
        // Acquisition PTS from capture time: dropped frames become
        // time gaps, never a sped-up recording. Keep PTS monotonic.
        ptsUs = Math.max(ptsUs, frame.ptsUs);
        codec.queueInputBuffer(inputIndex, 0, length, ptsUs, 0);
        drain(false);
    }

    private static void drain(boolean endOfStream) {
        long deadline = android.os.SystemClock.elapsedRealtime() + 5000;
        while (true) {
            if (endOfStream && android.os.SystemClock.elapsedRealtime() > deadline)
                throw new IllegalStateException("Encoder EOS timed out");
            int outputIndex = codec.dequeueOutputBuffer(bufferInfo, endOfStream ? 100_000 : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) return;
            } else if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (muxerStarted) throw new RuntimeException("format changed twice");
                trackIndex = muxer.addTrack(codec.getOutputFormat());
                muxer.start();
                muxerStarted = true;
            } else if (outputIndex >= 0) {
                ByteBuffer output = codec.getOutputBuffer(outputIndex);
                if (output != null && muxerStarted && bufferInfo.size > 0) {
                    output.position(bufferInfo.offset);
                    output.limit(bufferInfo.offset + bufferInfo.size);
                    muxer.writeSampleData(trackIndex, output, bufferInfo);
                }
                codec.releaseOutputBuffer(outputIndex, false);
                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) return;
            } else {
                return;
            }
        }
    }

    /**
     * RGBA (bottom-up) -> YUV420 into the encoder's padded input layout,
     * flipped vertically. Destination rows honour stride/sliceHeight;
     * semi-planar (NV12, interleaved U/V) unless the codec asked for
     * planar (I420, separate U and V planes).
     */
    private static void rgbaToNv12(byte[] rgba, int width, int height, byte[] nv12) {
        java.util.Arrays.fill(nv12, (byte) 0);
        int uvBase = stride * sliceHeight;
        int chromaRows = (sliceHeight + 1) / 2;
        for (int dstY = 0; dstY < height; dstY++) {
            int srcY = height - 1 - dstY;
            int srcRow = srcY * width * 4;
            int dstRow = dstY * stride;
            boolean sampleUv = (dstY % 2) == 0;
            for (int x = 0; x < width; x++) {
                int i = srcRow + x * 4;
                int r = rgba[i] & 0xFF, g = rgba[i + 1] & 0xFF, b = rgba[i + 2] & 0xFF;
                nv12[dstRow + x] = (byte) clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                if (sampleUv && (x % 2) == 0) {
                    byte u = (byte) clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    byte v = (byte) clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    if (planar) {
                        int uvRow = (dstY / 2) * stride + x / 2;
                        nv12[uvBase + uvRow] = u;
                        nv12[uvBase + stride * chromaRows + uvRow] = v;
                    } else {
                        int uvIndex = uvBase + (dstY / 2) * stride + x;
                        nv12[uvIndex] = u;     // NV12: U first
                        nv12[uvIndex + 1] = v;
                    }
                }
            }
        }
    }

    private static int clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
}
