﻿using System;
using System.Collections.Generic;
#if MONOMAC && PLATFORM_MACOS_LEGACY
using MonoMac.OpenAL;
#elif OPENAL
using OpenTK.Audio.OpenAL;
#endif

namespace Microsoft.Xna.Framework.Audio
{
    public sealed partial class DynamicSoundEffectInstance : SoundEffectInstance
    {
        private Queue<OALSoundBuffer> _queuedBuffers;
        private ALFormat _format;

        private void PlatformCreate()
        {
            _format = _channels == AudioChannels.Mono ? ALFormat.Mono16 : ALFormat.Stereo16;
            InitializeSound();

            SourceId = controller.ReserveSource();
            HasSourceId = true;

            _queuedBuffers = new Queue<OALSoundBuffer>();
        }

        private int PlatformGetPendingBufferCount()
        {
            return _queuedBuffers.Count;
        }

        private void PlatformPlay()
        {
            AL.GetError();
            AL.SourcePlay(SourceId);
            ALHelper.CheckError("Failed to play the source.");

            DynamicSoundEffectInstanceManager.AddInstance(this);
        }

        private void PlatformPause()
        {
            AL.GetError();
            AL.SourcePause(SourceId);
            ALHelper.CheckError("Failed to pause the source.");
        }

        private void PlatformResume()
        {
            AL.GetError();
            AL.SourcePlay(SourceId);
            ALHelper.CheckError("Failed to play the source.");
        }

        private void PlatformStop()
        {
            DynamicSoundEffectInstanceManager.RemoveInstance(this);

            AL.GetError();
            AL.SourceStop(SourceId);
            ALHelper.CheckError("Failed to stop the source.");

            // Remove all queued buffers
            AL.BindBufferToSource(SourceId, 0);
            while (_queuedBuffers.Count > 0)
            {
                var buffer = _queuedBuffers.Dequeue();
                buffer.Dispose();
            }
        }

        private void PlatformSubmitBuffer(byte[] buffer, int offset, int count)
        {
            // Get a buffer
            OALSoundBuffer oalBuffer = new OALSoundBuffer();

            // Bind the data
            if (offset == 0)
            {
                oalBuffer.BindDataBuffer(buffer, _format, count, _sampleRate);
            }
            else
            {
                // BindDataBuffer does not support offset
                var offsetBuffer = new byte[count];
                Array.Copy(buffer, offset, offsetBuffer, 0, count);
                oalBuffer.BindDataBuffer(offsetBuffer, _format, count, _sampleRate);
            }

            // Queue the buffer
            AL.SourceQueueBuffer(SourceId, oalBuffer.OpenALDataBuffer);
            ALHelper.CheckError();
            _queuedBuffers.Enqueue(oalBuffer);

            // If the source has run out of buffers, restart it
            if (_state == SoundState.Playing)
            {
                AL.SourcePlay(SourceId);
                ALHelper.CheckError("Failed to resume source playback.");
            }
        }

        private void PlatformDispose(bool disposing)
        {
            // Stop the source and bind null buffer so that it can be recycled
            AL.GetError();
            if (AL.IsSource(SourceId))
            {
                AL.SourceStop(SourceId);
                AL.BindBufferToSource(SourceId, 0);
                ALHelper.CheckError("Failed to stop the source.");
                controller.RecycleSource(SourceId);
            }
            
            if (disposing)
            {
                while (_queuedBuffers.Count > 0)
                {
                    var buffer = _queuedBuffers.Dequeue();
                    buffer.Dispose();
                }

                DynamicSoundEffectInstanceManager.RemoveInstance(this);
            }
        }

        internal void UpdateQueue()
        {
            // Get the completed buffers
            AL.GetError();
            int numBuffers;
            AL.GetSource(SourceId, ALGetSourcei.BuffersProcessed, out numBuffers);
            ALHelper.CheckError("Failed to get processed buffer count.");

            // Unqueue them
            if (numBuffers > 0)
            {
                AL.SourceUnqueueBuffers(SourceId, numBuffers);
                ALHelper.CheckError("Failed to unqueue buffers.");
                for (int i = 0; i < numBuffers; i++)
                {
                    var buffer = _queuedBuffers.Dequeue();
                    buffer.Dispose();
                }
            }

            // Raise the event for each removed buffer, if needed
            for (int i = 0; i < numBuffers; i++)
                CheckBufferCount();
            RaiseBufferNeeded();
        }
    }

    /// <summary>
    /// Handles the buffer events of all DynamicSoundEffectInstance instances.
    /// </summary>
    internal static class DynamicSoundEffectInstanceManager
    {
        private static List<WeakReference> _playingInstances;

        static DynamicSoundEffectInstanceManager()
        {
            _playingInstances = new List<WeakReference>();
        }

        public static void AddInstance(DynamicSoundEffectInstance instance)
        {
            var weakRef = new WeakReference(instance);
            _playingInstances.Add(weakRef);
        }

        public static void RemoveInstance(DynamicSoundEffectInstance instance)
        {
            for (int i = _playingInstances.Count - 1; i >= 0; i--)
            {
                if (_playingInstances[i].Target == instance)
                {
                    _playingInstances.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Updates buffer queues of the currently playing instances.
        /// </summary>
        /// <remarks>
        /// OpenAL does not implement callbacks as XAudio does, so this must be called periodically.
        /// </remarks>
        public static void UpdatePlayingInstances()
        {
            for (int i = _playingInstances.Count - 1; i >= 0; i--)
            {
                if (_playingInstances[i].IsAlive)
                {
                    var target = (DynamicSoundEffectInstance)_playingInstances[i].Target;
                    if (!target.IsDisposed)
                        target.UpdateQueue();
                }
                else
                {
                    // The instance has been disposed.
                    _playingInstances.RemoveAt(i);
                }
            }
        }
    }
}
