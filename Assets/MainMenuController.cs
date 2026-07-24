using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace DungeonGen
{
    /// <summary>
    /// Start-menu screen: one Play button. Press it, an intro video plays full-screen,
    /// and when it finishes the dungeon scene loads. The video's end (VideoPlayer's
    /// loopPointReached event) drives the load, not a hardcoded timer — the clip can
    /// be re-cut to any length later without touching this script.
    ///
    /// Lives on an empty GameObject in the menu scene. Doesn't build its own UI at
    /// runtime (unlike DungeonPlayerSpawner's code-built fallback player) — a menu is
    /// something you want to actually LOOK at, so author the Canvas/Button by hand in
    /// the editor and just wire the references here. "Build Menu UI In Scene" (right-
    /// click the component) drops in an unstyled placeholder button to get unblocked;
    /// restyle it freely afterward, or ignore it and assign your own Button.
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        [Header("UI")]
        [Tooltip("The Play button. Clicking it starts the intro video (or loads the dungeon directly if no video clip is assigned).")]
        public Button playButton;
        [Tooltip("Hidden as soon as Play is pressed, so the menu doesn't show through/behind the video.")]
        public GameObject menuRoot;

        [Header("Intro video")]
        [Tooltip("Plays full-screen (CameraFarPlane) behind everything on videoCamera. Left empty, Play loads the dungeon immediately.")]
        public VideoPlayer introVideo;
        [Tooltip("Camera the video renders onto. Left empty, uses Camera.main.")]
        public Camera videoCamera;
        [Tooltip("Routes the clip's audio through this AudioSource. Left empty, the video plays audio directly (VideoAudioOutputMode.Direct).")]
        public AudioSource videoAudioSource;

        [Header("Music")]
        [Tooltip("Menu background music. Faded out (not cut) the moment Play is pressed, so it doesn't clash with the video's own audio.")]
        public AudioSource menuMusic;
        [Tooltip("How long the fade-out takes, in seconds.")]
        public float musicFadeDuration = 1f;

        [Header("Scene")]
        [Tooltip("Name of the gameplay scene to load once the video ends (or immediately, if there's no video). Must be added to Build Settings.")]
        public string dungeonSceneName = "DungeonProc";

        void Awake()
        {
            if (videoCamera == null) videoCamera = Camera.main;

            if (introVideo != null)
            {
                introVideo.playOnAwake = false;
                introVideo.renderMode = VideoRenderMode.CameraFarPlane;
                introVideo.targetCamera = videoCamera;
                if (videoAudioSource != null)
                {
                    introVideo.audioOutputMode = VideoAudioOutputMode.AudioSource;
                    introVideo.SetTargetAudioSource(0, videoAudioSource);
                }
                else
                {
                    introVideo.audioOutputMode = VideoAudioOutputMode.Direct;
                }
                introVideo.loopPointReached += OnVideoFinished;
                introVideo.prepareCompleted += OnVideoPrepared;

                // Start decoding/buffering NOW, while the player is still looking at
                // the menu — Play() itself would trigger this preparation on the spot,
                // which is exactly the gap that showed raw skybox: the menu was already
                // hidden but the video had nothing to display yet. By the time someone
                // actually clicks Play this is almost always long since finished.
                introVideo.Prepare();
            }

            if (playButton != null)
                playButton.onClick.AddListener(OnPlayPressed);
        }

        bool videoReady;
        bool playRequested;

        void OnVideoPrepared(VideoPlayer vp)
        {
            videoReady = true;
            if (playRequested) StartVideo();
        }

        void OnPlayPressed()
        {
            if (menuMusic != null && menuMusic.isPlaying)
                StartCoroutine(FadeOutMusic());

            if (introVideo == null || introVideo.clip == null)
            {
                LoadDungeon();
                return;
            }

            // Don't hide the menu until the video can actually show something —
            // if it's not prepared yet (rare; Prepare() started at Awake), the menu
            // just stays up a beat longer instead of flashing to skybox.
            playRequested = true;
            if (videoReady) StartVideo();
        }

        void StartVideo()
        {
            if (menuRoot != null) menuRoot.SetActive(false);
            introVideo.Play();
        }

        IEnumerator FadeOutMusic()
        {
            float startVolume = menuMusic.volume;
            float t = 0f;
            while (t < musicFadeDuration)
            {
                t += Time.deltaTime;
                menuMusic.volume = Mathf.Lerp(startVolume, 0f, t / musicFadeDuration);
                yield return null;
            }
            menuMusic.Stop();
            menuMusic.volume = startVolume; // restore so a re-enter of the menu (or the same source reused later) isn't left silent
        }

        void OnVideoFinished(VideoPlayer vp) => LoadDungeon();

        void LoadDungeon() => SceneManager.LoadScene(dungeonSceneName);

        /// <summary>
        /// Drops in a bare, unstyled Play button (EventSystem + Canvas + Button + Text)
        /// so the menu is clickable immediately — restyle freely afterward, or delete
        /// it and wire your own Button to playButton. Safe to re-run; skips creating
        /// an EventSystem if one already exists in the scene.
        /// </summary>
        [ContextMenu("Build Menu UI In Scene")]
        void BuildMenuUI()
        {
            if (FindObjectOfType<EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<EventSystem>();
                esGo.AddComponent<StandaloneInputModule>();
            }

            var canvasGo = new GameObject("MenuCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            var buttonGo = new GameObject("PlayButton", typeof(RectTransform));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var rt = buttonGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(240, 80);
            rt.anchoredPosition = Vector2.zero;
            buttonGo.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            var button = buttonGo.AddComponent<Button>();

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(buttonGo.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var text = textGo.AddComponent<Text>();
            text.text = "PLAY";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;

            menuRoot = canvasGo;
            playButton = button;

            Debug.Log("[MainMenuController] Built a placeholder Play button — restyle freely, this is just to get you unblocked.", canvasGo);
        }
    }
}
