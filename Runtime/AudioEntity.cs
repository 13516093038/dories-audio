using System.Collections.Generic;
using Dories.Logging;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
#if DORIES_UNITASK_SUPPORT
using AudioClipTask = Cysharp.Threading.Tasks.UniTask<UnityEngine.AudioClip>;
using SoundTask = Cysharp.Threading.Tasks.UniTask<int>;
using AudioTask = Cysharp.Threading.Tasks.UniTask;
#else
using AudioClipTask = System.Threading.Tasks.Task<UnityEngine.AudioClip>;
using SoundTask = System.Threading.Tasks.Task<int>;
using AudioTask = System.Threading.Tasks.Task;
#endif

namespace Dories.Audio.Runtime
{
    /// <summary>
    /// 音效入口：背景音乐（两路 AudioSource 交叉淡入淡出）+ 音效（AudioSource 复用池）。
    /// AudioClip 按（包名, 地址）缓存并引用计数，不再使用且闲置超时后通过 <see cref="ClipProvider"/> 卸载。
    /// 各接口的 packageName 可选，为空时由 ClipProvider 决定（YooAsset 实现：Inspector 配置的包，再为空走默认包）。
    /// </summary>
    public class AudioEntity : MonoBehaviour
    {
        private const string Tag = "Audio";
        private const float IdleCheckInterval = 1f;

        [SerializeField, Range(0f, 1f)] private float _musicVolume = 1f;
        [SerializeField, Range(0f, 1f)] private float _soundVolume = 1f;
        [SerializeField] private bool _musicMuted;
        [SerializeField] private bool _soundMuted;

        [SerializeField, Tooltip("可选，音乐输出到的 Mixer 分组")] private AudioMixerGroup _musicMixerGroup;
        [SerializeField, Tooltip("可选，音效输出到的 Mixer 分组")] private AudioMixerGroup _soundMixerGroup;

        [SerializeField, Min(1), Tooltip("同时播放的音效上限，超出的新音效直接丢弃")]
        private int _maxSoundCount = 32;

        [SerializeField, Tooltip("AudioClip 无人使用且闲置超过该秒数后卸载；<=0 不自动释放")]
        private float _clipIdleReleaseSeconds = 60f;

        [SerializeField, Tooltip("场景卸载后释放所有无人使用的 AudioClip（KeepAlive 的除外）")]
        private bool _releaseUnusedOnSceneUnloaded = true;

        [SerializeReference, Tooltip("AudioClip 加载来源；留空时有 YooAssetSystem 则自动用场景里的 AssetLoaderEntity")]
        private IAudioClipProvider _clipProvider;

        internal sealed class ClipEntry
        {
            public AudioClip Clip;
            public int RefCount;
            public float LastUseTime;
            public bool KeepAlive;
        }

        internal sealed class MusicTrack
        {
            public AudioSource Source;
            public (string Package, string Location)? Key;
            public float Fade;
            public float Target;
            public float FadeSpeed;
        }

        internal sealed class SoundPlayback
        {
            public int Id;
            public AudioSource Source;
            public (string Package, string Location) Key;
            public float Volume;
        }

        private readonly Dictionary<(string Package, string Location), ClipEntry> _clips = new();
        private readonly List<SoundPlayback> _sounds = new();
        private readonly MusicTrack[] _musicTracks = { new(), new() };
        private ObjectPool<AudioSource> _soundSourcePool;
        private int _currentTrack;
        private int _musicRequest;
        private int _nextSoundId;
        private float _nextIdleCheckTime;
        private bool _isQuitting;

        /// <summary>
        /// AudioClip 加载来源，可在 Inspector 选择，需在第一次播放前设置，之后不要更换。
        /// </summary>
        public IAudioClipProvider ClipProvider
        {
            get => _clipProvider;
            set => _clipProvider = value;
        }

        internal Dictionary<(string Package, string Location), ClipEntry> Clips => _clips;
        internal List<SoundPlayback> Sounds => _sounds;
        internal MusicTrack CurrentMusicTrack => _musicTracks[_currentTrack];

        public int ActiveSoundCount => _sounds.Count;

        /// <summary>
        /// 当前（或正在淡入的）背景音乐地址，没有时为 null。
        /// </summary>
        public string CurrentMusic => CurrentMusicTrack.Key?.Location;

        private static (string, string) ClipKey(string packageName, string location) => (packageName ?? string.Empty, location);

        internal static string DisplayName((string Package, string Location) key) =>
            string.IsNullOrEmpty(key.Package) ? key.Location : $"{key.Package}/{key.Location}";

        #region Volume

        public float GetVolume(AudioChannel channel) => channel == AudioChannel.Music ? _musicVolume : _soundVolume;

        public bool IsMuted(AudioChannel channel) => channel == AudioChannel.Music ? _musicMuted : _soundMuted;

        public void SetVolume(AudioChannel channel, float volume)
        {
            volume = Mathf.Clamp01(volume);
            if (channel == AudioChannel.Music)
                _musicVolume = volume;
            else
                _soundVolume = volume;
            ApplyVolumes();
        }

        public void SetMute(AudioChannel channel, bool muted)
        {
            if (channel == AudioChannel.Music)
                _musicMuted = muted;
            else
                _soundMuted = muted;
            ApplyVolumes();
        }

        private float ChannelVolume(AudioChannel channel) => IsMuted(channel) ? 0f : GetVolume(channel);

        private void ApplyVolumes()
        {
            foreach (var track in _musicTracks)
            {
                if (track.Source != null)
                    track.Source.volume = track.Fade * ChannelVolume(AudioChannel.Music);
            }

            float soundVolume = ChannelVolume(AudioChannel.Sound);
            foreach (var sound in _sounds)
                sound.Source.volume = sound.Volume * soundVolume;
        }

        #endregion

        #region Music

        /// <summary>
        /// 播放背景音乐，fade 秒内与当前音乐交叉淡入淡出。请求的音乐正在播放时不重播。
        /// </summary>
        public async AudioTask PlayMusicAsync(string location, float fade = 1f, bool loop = true, string packageName = null)
        {
            var key = ClipKey(packageName, location);
            if (CurrentMusicTrack.Key == key && CurrentMusicTrack.Target > 0f)
                return;

            // 加载期间又发起了新请求，以最后一次为准
            int request = ++_musicRequest;
            var clip = await AcquireClipAsync(packageName, location);
            if (clip == null)
                return;
            if (this == null || request != _musicRequest)
            {
                ReleaseClip(key);
                return;
            }

            SetTrackTarget(CurrentMusicTrack, 0f, fade);

            _currentTrack = 1 - _currentTrack;
            var track = CurrentMusicTrack;
            // 新轨道若还在淡出旧曲，直接停掉
            StopTrack(track);
            EnsureMusicSource(track);
            track.Key = key;
            track.Fade = 0f;
            track.Source.clip = clip;
            track.Source.loop = loop;
            track.Source.Play();
            SetTrackTarget(track, 1f, fade);
        }

        /// <summary>
        /// 停止背景音乐，fade 秒内淡出。
        /// </summary>
        public void StopMusic(float fade = 1f)
        {
            ++_musicRequest;
            SetTrackTarget(CurrentMusicTrack, 0f, fade);
        }

        public void PauseMusic()
        {
            foreach (var track in _musicTracks)
            {
                if (track.Source != null)
                    track.Source.Pause();
            }
        }

        public void ResumeMusic()
        {
            foreach (var track in _musicTracks)
            {
                if (track.Source != null)
                    track.Source.UnPause();
            }
        }

        private void EnsureMusicSource(MusicTrack track)
        {
            if (track.Source != null)
                return;

            track.Source = CreateSource("Music", _musicMixerGroup);
        }

        private void SetTrackTarget(MusicTrack track, float target, float fade)
        {
            track.Target = target;
            track.FadeSpeed = fade > 0f ? 1f / fade : 0f;
            if (fade <= 0f)
                track.Fade = target;
            UpdateTrack(track, 0f);
        }

        private void UpdateTrack(MusicTrack track, float deltaTime)
        {
            if (track.Key == null)
                return;

            if (track.FadeSpeed > 0f)
                track.Fade = Mathf.MoveTowards(track.Fade, track.Target, track.FadeSpeed * deltaTime);
            track.Source.volume = track.Fade * ChannelVolume(AudioChannel.Music);

            if (track.Target <= 0f && track.Fade <= 0f)
                StopTrack(track);
        }

        private void StopTrack(MusicTrack track)
        {
            if (track.Key is not { } key)
                return;

            track.Source.Stop();
            track.Source.clip = null;
            track.Key = null;
            track.Fade = track.Target = 0f;
            ReleaseClip(key);
        }

        #endregion

        #region Sound

        /// <summary>
        /// 播放 2D 音效，返回播放 id（用于 StopSound）；失败或超过并发上限返回 0。
        /// </summary>
        public SoundTask PlaySoundAsync(string location, float volume = 1f, bool loop = false, string packageName = null)
            => PlaySoundCoreAsync(location, null, volume, loop, packageName);

        /// <summary>
        /// 在世界坐标 position 播放 3D 音效。
        /// </summary>
        public SoundTask PlaySoundAtAsync(string location, Vector3 position, float volume = 1f, bool loop = false, string packageName = null)
            => PlaySoundCoreAsync(location, position, volume, loop, packageName);

        private async SoundTask PlaySoundCoreAsync(string location, Vector3? position, float volume, bool loop, string packageName)
        {
            var clip = await AcquireClipAsync(packageName, location);
            if (clip == null)
                return 0;

            var key = ClipKey(packageName, location);
            if (this == null)
            {
                ReleaseClip(key);
                return 0;
            }

            if (_sounds.Count >= _maxSoundCount)
            {
                Log.Debug(Tag, $"音效数量达到上限 {_maxSoundCount}，丢弃: {DisplayName(key)}", this);
                ReleaseClip(key);
                return 0;
            }

            _soundSourcePool ??= new ObjectPool<AudioSource>(
                () => CreateSource("Sound", _soundMixerGroup),
                actionOnRelease: s => { s.Stop(); s.clip = null; },
                actionOnDestroy: s => DestroyObject(s.gameObject),
                collectionCheck: false,
                maxSize: _maxSoundCount);

            var source = _soundSourcePool.Get();
            source.clip = clip;
            source.loop = loop;
            source.spatialBlend = position.HasValue ? 1f : 0f;
            source.transform.position = position ?? Vector3.zero;
            source.volume = volume * ChannelVolume(AudioChannel.Sound);
            source.Play();

            // id 只增不复用，0 留作无效值
            if (++_nextSoundId <= 0)
                _nextSoundId = 1;
            _sounds.Add(new SoundPlayback { Id = _nextSoundId, Source = source, Key = key, Volume = volume });
            return _nextSoundId;
        }

        /// <summary>
        /// 停止指定音效，id 已结束或无效时忽略。
        /// </summary>
        public void StopSound(int id)
        {
            int index = _sounds.FindIndex(s => s.Id == id);
            if (index >= 0)
                RecycleSound(index);
        }

        public void StopAllSounds()
        {
            for (int i = _sounds.Count - 1; i >= 0; i--)
                RecycleSound(i);
        }

        private void RecycleSound(int index)
        {
            var sound = _sounds[index];
            _sounds.RemoveAt(index);
            if (sound.Source != null)
                _soundSourcePool.Release(sound.Source);
            ReleaseClip(sound.Key);
        }

        private AudioSource CreateSource(string name, AudioMixerGroup group)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.outputAudioMixerGroup = group;
            return source;
        }

        #endregion

        #region Clip Cache

        /// <summary>
        /// 预加载 AudioClip。keepAlive 为 true 时不参与空闲释放与场景卸载释放。
        /// </summary>
        public async AudioTask PreloadAsync(string location, bool keepAlive = false, string packageName = null)
        {
            if (await AcquireClipAsync(packageName, location) == null)
                return;

            var key = ClipKey(packageName, location);
            if (_clips.TryGetValue(key, out var entry))
                entry.KeepAlive |= keepAlive;
            ReleaseClip(key);
        }

        /// <summary>
        /// 取 AudioClip 并加一次引用；失败返回 null。
        /// </summary>
        private async AudioClipTask AcquireClipAsync(string packageName, string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                Log.Error(Tag, "Audio location is null or empty", this);
                return null;
            }

            var key = ClipKey(packageName, location);
            if (_clips.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                return entry.Clip;
            }

            if (ClipProvider == null)
            {
                Log.Error(Tag, $"ClipProvider 未设置，无法加载: {DisplayName(key)}", this);
                return null;
            }

            var clip = await ClipProvider.LoadAsync(packageName, location);

            // 加载期间本组件已销毁或加载结果为空：退还这次拿到的引用
            if (this == null || clip == null)
            {
                ClipProvider.Unload(packageName, location);
                if (clip == null)
                    Log.Error(Tag, $"AudioClip 加载失败: {DisplayName(key)}");
                return null;
            }

            // 并发加载时别人先缓存了：退还多拿的一份引用
            if (_clips.TryGetValue(key, out entry))
            {
                ClipProvider.Unload(packageName, location);
                entry.RefCount++;
                return entry.Clip;
            }

            _clips.Add(key, new ClipEntry { Clip = clip, RefCount = 1, LastUseTime = Time.unscaledTime });
            return clip;
        }

        private void ReleaseClip((string Package, string Location) key)
        {
            if (!_clips.TryGetValue(key, out var entry))
                return;

            entry.RefCount--;
            entry.LastUseTime = Time.unscaledTime;
        }

        /// <summary>
        /// 卸载所有无人使用且未标记 KeepAlive 的 AudioClip。
        /// </summary>
        public void ReleaseUnusedClips() => UnloadClipsWhere(e => e.RefCount <= 0 && !e.KeepAlive);

        internal void ReleaseIdleClips(float now)
        {
            if (_clipIdleReleaseSeconds > 0f)
                UnloadClipsWhere(e => e.RefCount <= 0 && !e.KeepAlive && now - e.LastUseTime >= _clipIdleReleaseSeconds);
        }

        private void UnloadClipsWhere(System.Predicate<ClipEntry> match)
        {
            var keys = ListPool<(string, string)>.Get();
            foreach (var pair in _clips)
            {
                if (match(pair.Value))
                    keys.Add(pair.Key);
            }
            foreach (var key in keys)
            {
                _clips.Remove(key);
                ClipProvider?.Unload(key.Item1, key.Item2);
            }
            ListPool<(string, string)>.Release(keys);
        }

        /// <summary>
        /// 停止所有音乐和音效并卸载全部 AudioClip。
        /// </summary>
        public void ReleaseAll()
        {
            ++_musicRequest;
            foreach (var track in _musicTracks)
                StopTrack(track);
            StopAllSounds();
            UnloadClipsWhere(_ => true);
        }

        private static void DestroyObject(GameObject go)
        {
            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        #endregion

        #region Unity

        private void Awake()
        {
#if DORIES_YOOASSET_SUPPORT
            // 没配置时默认走 YooAsset，加载器在首次加载时从场景里找
            _clipProvider ??= new YooAssetAudioClipProvider();
#endif
        }

        // Inspector 里改音量/静音时即时生效
        private void OnValidate()
        {
            if (Application.isPlaying)
                ApplyVolumes();
        }

        private void OnEnable() => SceneManager.sceneUnloaded += OnSceneUnloaded;

        private void OnDisable() => SceneManager.sceneUnloaded -= OnSceneUnloaded;

        // 退出时 YooAsset 可能先于本组件销毁，再 Unload 会抛异常；进程结束资源自会回收
        private void OnApplicationQuit() => _isQuitting = true;

        private void OnDestroy()
        {
            if (!_isQuitting)
                ReleaseAll();
        }

        private void Update()
        {
            float deltaTime = Time.unscaledDeltaTime;
            foreach (var track in _musicTracks)
                UpdateTrack(track, deltaTime);

            // AudioListener.pause 时 isPlaying 为 false，不能当作播完回收
            if (!AudioListener.pause)
            {
                for (int i = _sounds.Count - 1; i >= 0; i--)
                {
                    if (_sounds[i].Source == null || !_sounds[i].Source.isPlaying)
                        RecycleSound(i);
                }
            }

            float now = Time.unscaledTime;
            if (now < _nextIdleCheckTime)
                return;

            _nextIdleCheckTime = now + IdleCheckInterval;
            ReleaseIdleClips(now);
        }

        internal void OnSceneUnloaded(Scene scene)
        {
            if (_releaseUnusedOnSceneUnloaded)
                ReleaseUnusedClips();
        }

        #endregion
    }
}
