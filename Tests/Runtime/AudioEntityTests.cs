using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Dories.Audio.Runtime;
using NUnit.Framework;
using UnityEngine;

namespace Dories.Audio.Tests
{
    public class AudioEntityTests
    {
        private const string Sfx = "Audio/Click";
        private const string BgmA = "Audio/BgmA";
        private const string BgmB = "Audio/BgmB";

        private class FakeClipProvider : IAudioClipProvider
        {
            public readonly Dictionary<string, AudioClip> Clips = new();
            public readonly Queue<UniTask<AudioClip>> Pending = new();
            public readonly List<string> Loaded = new();
            public readonly List<string> Unloaded = new();

            public UniTask<AudioClip> LoadAsync(string packageName, string location)
            {
                Loaded.Add(location);
                if (Pending.Count > 0)
                    return Pending.Dequeue();
                return UniTask.FromResult(Clips[location]);
            }

            public void Unload(string packageName, string location) => Unloaded.Add(location);
        }

        private AudioEntity _entity;
        private FakeClipProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _provider = new FakeClipProvider();
            foreach (var name in new[] { Sfx, BgmA, BgmB })
                _provider.Clips[name] = AudioClip.Create(name, 4410, 1, 44100, false);

            _entity = new GameObject("Audio").AddComponent<AudioEntity>();
            _entity.ClipProvider = _provider;
        }

        [TearDown]
        public void TearDown()
        {
            _entity.ReleaseAll();
            Object.DestroyImmediate(_entity.gameObject);
            foreach (var clip in _provider.Clips.Values)
                Object.DestroyImmediate(clip);
        }

        private static T Run<T>(UniTask<T> task) => task.GetAwaiter().GetResult();
        private static void Run(UniTask task) => task.GetAwaiter().GetResult();

        private int RefCount(string location) => _entity.Clips[(string.Empty, location)].RefCount;

        [Test]
        public void PlaySound_LoadsClipOnce_AndReturnsDistinctIds()
        {
            int a = Run(_entity.PlaySoundAsync(Sfx));
            int b = Run(_entity.PlaySoundAsync(Sfx));

            Assert.AreNotEqual(0, a);
            Assert.AreNotEqual(a, b);
            Assert.AreEqual(2, _entity.ActiveSoundCount);
            Assert.AreEqual(1, _provider.Loaded.Count);
            Assert.AreEqual(2, RefCount(Sfx));
        }

        [Test]
        public void StopSound_ReleasesRef_ThenReleaseUnusedUnloads()
        {
            int id = Run(_entity.PlaySoundAsync(Sfx));

            _entity.StopSound(id);
            _entity.StopSound(id);

            Assert.AreEqual(0, _entity.ActiveSoundCount);
            Assert.AreEqual(0, RefCount(Sfx));

            _entity.ReleaseUnusedClips();
            CollectionAssert.AreEqual(new[] { Sfx }, _provider.Unloaded);
        }

        [Test]
        public void PlaySound_OverLimit_IsDropped()
        {
            var so = new UnityEditor.SerializedObject(_entity);
            so.FindProperty("_maxSoundCount").intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();

            Run(_entity.PlaySoundAsync(Sfx));
            int dropped = Run(_entity.PlaySoundAsync(Sfx));

            Assert.AreEqual(0, dropped);
            Assert.AreEqual(1, _entity.ActiveSoundCount);
            Assert.AreEqual(1, RefCount(Sfx));
        }

        [Test]
        public void SetVolume_And_Mute_ApplyToPlayingSound()
        {
            int id = Run(_entity.PlaySoundAsync(Sfx, volume: 0.5f));
            var source = _entity.Sounds.Find(s => s.Id == id).Source;

            _entity.SetVolume(AudioChannel.Sound, 0.5f);
            Assert.AreEqual(0.25f, source.volume, 1e-4f);

            _entity.SetMute(AudioChannel.Sound, true);
            Assert.AreEqual(0f, source.volume, 1e-4f);
        }

        [Test]
        public void PlayMusic_SwitchWithoutFade_StopsOldAndReleasesItsClip()
        {
            Run(_entity.PlayMusicAsync(BgmA, fade: 0f));
            Assert.AreEqual(BgmA, _entity.CurrentMusic);

            Run(_entity.PlayMusicAsync(BgmB, fade: 0f));

            Assert.AreEqual(BgmB, _entity.CurrentMusic);
            Assert.AreEqual(0, RefCount(BgmA));
            Assert.AreEqual(1, RefCount(BgmB));
        }

        [Test]
        public void PlayMusic_SameMusic_DoesNotReload()
        {
            Run(_entity.PlayMusicAsync(BgmA, fade: 0f));
            Run(_entity.PlayMusicAsync(BgmA, fade: 0f));

            Assert.AreEqual(1, _provider.Loaded.Count);
            Assert.AreEqual(1, RefCount(BgmA));
        }

        [Test]
        public void PlayMusic_StaleRequest_IsDiscarded()
        {
            var a = new UniTaskCompletionSource<AudioClip>();
            var b = new UniTaskCompletionSource<AudioClip>();
            _provider.Pending.Enqueue(a.Task);
            _provider.Pending.Enqueue(b.Task);

            var playA = _entity.PlayMusicAsync(BgmA, fade: 0f);
            var playB = _entity.PlayMusicAsync(BgmB, fade: 0f);
            b.TrySetResult(_provider.Clips[BgmB]);
            a.TrySetResult(_provider.Clips[BgmA]);
            Run(playA);
            Run(playB);

            Assert.AreEqual(BgmB, _entity.CurrentMusic);
            Assert.AreEqual(0, RefCount(BgmA));
        }

        [Test]
        public void StopMusic_WithoutFade_ReleasesClip()
        {
            Run(_entity.PlayMusicAsync(BgmA, fade: 0f));

            _entity.StopMusic(0f);

            Assert.IsNull(_entity.CurrentMusic);
            Assert.AreEqual(0, RefCount(BgmA));
        }

        [Test]
        public void Preload_KeepAlive_SurvivesReleaseUnusedAndIdle()
        {
            Run(_entity.PreloadAsync(Sfx, keepAlive: true));
            Run(_entity.PreloadAsync(BgmA));

            _entity.ReleaseIdleClips(Time.unscaledTime + 1000f);

            Assert.IsTrue(_entity.Clips.ContainsKey((string.Empty, Sfx)));
            Assert.IsFalse(_entity.Clips.ContainsKey((string.Empty, BgmA)));
            CollectionAssert.AreEqual(new[] { BgmA }, _provider.Unloaded);
        }

        [Test]
        public void SameLocationInDifferentPackages_AreCachedSeparately()
        {
            Run(_entity.PlaySoundAsync(Sfx));
            Run(_entity.PlaySoundAsync(Sfx, packageName: "DLC1"));

            Assert.AreEqual(2, _provider.Loaded.Count);
            Assert.IsTrue(_entity.Clips.ContainsKey(("DLC1", Sfx)));
        }
    }
}
