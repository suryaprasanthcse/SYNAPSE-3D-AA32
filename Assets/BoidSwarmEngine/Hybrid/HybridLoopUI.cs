using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The hybrid loop's presentation layer: act title cards, the card-scan prompt, the TASK COMPLETED beat, the
    /// loading screen during swarm transitions, and a one-line hint.
    ///
    /// Built from code on first use rather than authored in the scene, for the same reason as MissionHeader: it is
    /// real uGUI on the built-in font, so there is no TextMeshPro resource import to forget and no scene wiring to
    /// break. Nothing here takes raycasts - the floating joystick reads touches directly and must never have a
    /// full-screen panel swallow them.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Hybrid Loop UI")]
    public sealed class HybridLoopUI : MonoBehaviour
    {
        [SerializeField] Color accent = new(0.40f, 0.92f, 1.00f, 1f);
        [SerializeField] Color gold = new(1.00f, 0.80f, 0.35f, 1f);
        [SerializeField] Color muted = new(0.72f, 0.78f, 0.84f, 1f);
        [SerializeField] int sortingOrder = 40;

        [Header("Watermark")]
        [SerializeField] string watermark = "Team Nandha - ML";
        [SerializeField, Min(0f)] float watermarkPulseHz = 0.4f;

        Font font;
        bool built;
        CanvasScaler scaler;
        bool landscape;

        CanvasGroup titleGroup, scanGroup, completeGroup, loadingGroup, hintGroup;
        Text actLabel, actTitle, actSubtitle;
        Image titleRule;
        Text scanHeading, scanCardName, scanStatus;
        RawImage scanThumb;
        readonly List<Image> reticle = new();
        Text completeTitle, completeDetail;
        Text loadingLabel, loadingDetail;
        Image loadingFill;
        Text hintText;
        CanvasGroup newGameGroup;
        Button newGameButton;
        Text watermarkText;
        readonly List<Outline> watermarkGlow = new();

        readonly Dictionary<CanvasGroup, Coroutine> fades = new();
        bool loadingVisible;
        bool scanDetected;

        void Awake() => Build();

        void Update()
        {
            if (!built) return;
            FitOrientation();
            float t = Time.unscaledTime;

            if (loadingVisible)
                loadingLabel.text = "L O A D I N G" + new string('.', 1 + (int)(t * 2.5f) % 3);

            // The watermark's glow breathes: the halo layers swell and fade while the letters stay crisp.
            float breath = 0.5f + 0.5f * Mathf.Sin(t * watermarkPulseHz * Mathf.PI * 2f);
            for (int i = 0; i < watermarkGlow.Count; i++)
            {
                float strength = (i == 0 ? 0.55f : 0.22f) * Mathf.Lerp(0.45f, 1f, breath);
                watermarkGlow[i].effectColor = WithAlpha(accent, strength);
            }

            if (scanGroup.alpha > 0.01f && !scanDetected)
            {
                // The reticle breathes while searching, so a static screen never reads as a hang.
                float a = 0.55f + 0.45f * Mathf.Sin(t * 4f);
                foreach (var bar in reticle) bar.color = WithAlpha(accent, a);
                scanStatus.color = WithAlpha(muted, 0.6f + 0.4f * Mathf.Sin(t * 3f));
            }
        }

        // ------------------------------------------------------------------------------------------
        // Act title
        // ------------------------------------------------------------------------------------------
        /// <summary>Centre-screen cinematic title: fades in, holds, fades out.</summary>
        public IEnumerator PlayActTitle(string label, string title, string subtitle, float holdSeconds, bool golden = false)
        {
            Build();
            Color c = golden ? gold : accent;
            actLabel.text = Spaced(label);
            actLabel.color = c;
            actTitle.text = title;
            actSubtitle.text = subtitle;
            titleRule.color = WithAlpha(c, 0.85f);

            yield return Fade(titleGroup, 1f, 0.6f);
            yield return new WaitForSecondsRealtime(holdSeconds);
            yield return Fade(titleGroup, 0f, 0.9f);
        }

        // ------------------------------------------------------------------------------------------
        // Card scan
        // ------------------------------------------------------------------------------------------
        public void ShowScanPrompt(string cardRole, string cardName, Texture cardArt)
        {
            Build();
            scanDetected = false;
            scanHeading.text = $"SCAN THE {cardRole} CARD";
            scanCardName.text = cardName;
            scanStatus.text = "World locked  ·  point your camera at the card";
            scanThumb.texture = cardArt;
            scanThumb.enabled = cardArt != null;
            StartFade(scanGroup, 1f, 0.35f);
        }

        public void SetScanStatus(string status)
        {
            Build();
            scanStatus.text = status;
        }

        public void MarkScanDetected()
        {
            scanDetected = true;
            scanStatus.text = "CARD RECOGNISED";
            scanStatus.color = gold;
            foreach (var bar in reticle) bar.color = gold;
        }

        public void HideScanPrompt() => StartFade(scanGroup, 0f, 0.3f);

        // ------------------------------------------------------------------------------------------
        // Task completed
        // ------------------------------------------------------------------------------------------
        public IEnumerator PlayTaskCompleted(string detail, float holdSeconds = 1.6f)
        {
            Build();
            completeTitle.text = "TASK COMPLETED";
            completeDetail.text = detail;
            completeGroup.transform.localScale = Vector3.one * 1.08f;

            float t = 0f;
            StartFade(completeGroup, 1f, 0.25f);
            while (t < 0.35f)
            {
                t += Time.unscaledDeltaTime;
                completeGroup.transform.localScale = Vector3.one * Mathf.Lerp(1.08f, 1f, t / 0.35f);
                yield return null;
            }
            completeGroup.transform.localScale = Vector3.one;
            yield return new WaitForSecondsRealtime(holdSeconds);
            yield return Fade(completeGroup, 0f, 0.6f);
        }

        // ------------------------------------------------------------------------------------------
        // Loading
        // ------------------------------------------------------------------------------------------
        public void ShowLoading(bool visible, string detail = "The swarm is crossing the room")
        {
            Build();
            loadingVisible = visible;
            if (visible)
            {
                loadingDetail.text = detail;
                SetLoadingProgress(0f);
            }
            StartFade(loadingGroup, visible ? 1f : 0f, visible ? 0.45f : 0.7f);
        }

        public void SetLoadingProgress(float progress)
        {
            if (loadingFill != null) loadingFill.fillAmount = Mathf.Clamp01(progress);
        }

        // ------------------------------------------------------------------------------------------
        // Hint
        // ------------------------------------------------------------------------------------------
        public void ShowHint(string text)
        {
            Build();
            hintText.text = text;
            StartFade(hintGroup, 1f, 0.4f);
        }

        public void HideHint() => StartFade(hintGroup, 0f, 0.4f);

        // ------------------------------------------------------------------------------------------
        // New game
        // ------------------------------------------------------------------------------------------
        /// <summary>Offer a fresh run once the story is over. The one control on this canvas that takes a tap.</summary>
        public void ShowNewGame(Action onNewGame)
        {
            Build();
            EnsureEventSystem();
            newGameButton.onClick.RemoveAllListeners();
            newGameButton.onClick.AddListener(() =>
            {
                newGameButton.interactable = false;
                onNewGame?.Invoke();
            });
            newGameButton.interactable = true;
            newGameGroup.interactable = true;
            newGameGroup.blocksRaycasts = true;
            StartFade(newGameGroup, 1f, 0.6f);
        }

        /// <summary>The scene has no EventSystem (nothing else needs one); a button does.</summary>
        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(null);
        }

        // ------------------------------------------------------------------------------------------
        // Fading
        // ------------------------------------------------------------------------------------------
        void StartFade(CanvasGroup group, float target, float seconds)
        {
            if (fades.TryGetValue(group, out var running) && running != null) StopCoroutine(running);
            fades[group] = StartCoroutine(FadeRoutine(group, target, seconds));
        }

        IEnumerator Fade(CanvasGroup group, float target, float seconds)
        {
            StartFade(group, target, seconds);
            // Timed, not "until alpha arrives": if another fade retargets this group meanwhile (a scan hiding an
            // act title), waiting on the alpha would hang the caller forever.
            yield return new WaitForSecondsRealtime(seconds);
        }

        /// <summary>Clear the act title early, e.g. when a card scan takes over the screen.</summary>
        public void HideActTitle()
        {
            Build();
            StartFade(titleGroup, 0f, 0.25f);
        }

        static IEnumerator FadeRoutine(CanvasGroup group, float target, float seconds)
        {
            float start = group.alpha;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(seconds, 1e-3f);
                group.alpha = Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t)));
                yield return null;
            }
            group.alpha = target;
        }

        // ------------------------------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------------------------------
        void Build()
        {
            if (built) return;
            built = true;

            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var canvasGo = new GameObject("HybridLoop_Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            landscape = Screen.width <= Screen.height;      // force the first fit to apply
            FitOrientation();
            Transform root = canvasGo.transform;

            // --- Act title: dead centre, thin rule between label and title ---
            titleGroup = Group(root, "ActTitle");
            actLabel = Label(titleGroup.transform, "Label", 34, FontStyle.Normal, new Vector2(0f, 110f), accent);
            actTitle = Label(titleGroup.transform, "Title", 82, FontStyle.Bold, new Vector2(0f, 20f), Color.white);
            titleRule = Bar(titleGroup.transform, "Rule", new Vector2(0f, 72f), new Vector2(320f, 3f), accent);
            actSubtitle = Label(titleGroup.transform, "Subtitle", 30, FontStyle.Italic, new Vector2(0f, -60f), muted);

            // --- Scan prompt: an empty card-shaped reticle (the camera feed must stay clear where the player
            //     aims), with a small thumbnail of the wanted card tucked against its corner ---
            scanGroup = Group(root, "ScanPrompt");
            Vector2 frame = new(420f, 600f);
            BuildReticle(scanGroup.transform, frame);
            Vector2 thumbSize = new(96f, 140f);
            Bar(scanGroup.transform, "ThumbBack", new Vector2(-frame.x * 0.5f - thumbSize.x * 0.5f - 24f, frame.y * 0.5f - thumbSize.y * 0.5f),
                thumbSize + new Vector2(8f, 8f), new Color(0f, 0f, 0f, 0.6f));
            scanThumb = new GameObject("CardThumb", typeof(RectTransform)).AddComponent<RawImage>();
            Place(scanThumb.rectTransform, scanGroup.transform,
                  new Vector2(-frame.x * 0.5f - thumbSize.x * 0.5f - 24f, frame.y * 0.5f - thumbSize.y * 0.5f), thumbSize);
            scanThumb.raycastTarget = false;
            scanHeading = Label(scanGroup.transform, "Heading", 44, FontStyle.Bold, new Vector2(0f, frame.y * 0.5f + 84f), accent);
            scanCardName = Label(scanGroup.transform, "Card", 32, FontStyle.Normal, new Vector2(0f, frame.y * 0.5f + 34f), Color.white);
            scanStatus = Label(scanGroup.transform, "Status", 28, FontStyle.Normal, new Vector2(0f, -frame.y * 0.5f - 44f), muted);

            // --- Task completed: a dark band across the middle ---
            completeGroup = Group(root, "TaskCompleted");
            Bar(completeGroup.transform, "Band", Vector2.zero, new Vector2(4000f, 330f), new Color(0f, 0f, 0f, 0.55f));
            Bar(completeGroup.transform, "RuleTop", new Vector2(0f, 150f), new Vector2(4000f, 2f), WithAlpha(gold, 0.6f));
            Bar(completeGroup.transform, "RuleBottom", new Vector2(0f, -150f), new Vector2(4000f, 2f), WithAlpha(gold, 0.6f));
            completeTitle = Label(completeGroup.transform, "Title", 92, FontStyle.Bold, new Vector2(0f, 30f), gold);
            completeDetail = Label(completeGroup.transform, "Detail", 32, FontStyle.Normal, new Vector2(0f, -60f), accent);

            // --- Loading: a light scrim, so the swarm stays visible crossing the room behind it ---
            loadingGroup = Group(root, "Loading");
            Bar(loadingGroup.transform, "Scrim", Vector2.zero, new Vector2(8000f, 8000f), new Color(0f, 0f, 0f, 0.4f));
            loadingLabel = Label(loadingGroup.transform, "Label", 50, FontStyle.Bold, new Vector2(0f, 30f), Color.white);
            Bar(loadingGroup.transform, "Track", new Vector2(0f, -40f), new Vector2(440f, 4f), new Color(1f, 1f, 1f, 0.15f));
            loadingFill = Bar(loadingGroup.transform, "Fill", new Vector2(0f, -40f), new Vector2(440f, 4f), accent);
            loadingFill.type = Image.Type.Filled;
            loadingFill.fillMethod = Image.FillMethod.Horizontal;
            loadingFill.sprite = WhiteSprite();
            loadingDetail = Label(loadingGroup.transform, "Detail", 26, FontStyle.Italic, new Vector2(0f, -95f), muted);

            // --- Hint: bottom of the screen, above the joystick's natural thumb zone ---
            hintGroup = Group(root, "Hint");
            var hintRect = (RectTransform)hintGroup.transform;
            hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 0f);
            hintRect.anchoredPosition = new Vector2(0f, 360f);
            Bar(hintGroup.transform, "Pill", Vector2.zero, new Vector2(900f, 80f), new Color(0f, 0f, 0f, 0.5f));
            hintText = Label(hintGroup.transform, "Text", 32, FontStyle.Normal, Vector2.zero, Color.white);

            // --- New game: under the epilogue title, shown only once the story is complete ---
            canvasGo.AddComponent<GraphicRaycaster>();
            newGameGroup = Group(root, "NewGame");
            ((RectTransform)newGameGroup.transform).anchoredPosition = new Vector2(0f, -230f);
            var buttonFrame = Bar(newGameGroup.transform, "Frame", Vector2.zero, new Vector2(420f, 110f), WithAlpha(accent, 0.9f));
            var face = Bar(newGameGroup.transform, "Face", Vector2.zero, new Vector2(412f, 102f), new Color(0.02f, 0.05f, 0.08f, 0.92f));
            face.raycastTarget = true;
            newGameButton = face.gameObject.AddComponent<Button>();
            newGameButton.targetGraphic = face;
            var colors = newGameButton.colors;
            colors.highlightedColor = new Color(0.75f, 0.95f, 1f);
            colors.pressedColor = new Color(0.5f, 0.85f, 1f);
            newGameButton.colors = colors;
            Label(newGameGroup.transform, "Label", 40, FontStyle.Bold, Vector2.zero, Color.white).text = "N E W   G A M E";
            buttonFrame.raycastTarget = false;

            // --- Watermark: bottom-left, always on, glowing ---
            var mark = new GameObject("Watermark", typeof(RectTransform));
            var markRect = (RectTransform)mark.transform;
            markRect.SetParent(root, false);
            markRect.anchorMin = markRect.anchorMax = new Vector2(0f, 0f);
            markRect.pivot = new Vector2(0f, 0f);
            markRect.anchoredPosition = new Vector2(34f, 26f);
            markRect.sizeDelta = new Vector2(600f, 60f);
            watermarkText = mark.AddComponent<Text>();
            watermarkText.font = font;
            watermarkText.fontSize = 30;
            watermarkText.fontStyle = FontStyle.Bold;
            watermarkText.alignment = TextAnchor.LowerLeft;
            watermarkText.horizontalOverflow = HorizontalWrapMode.Overflow;
            watermarkText.verticalOverflow = VerticalWrapMode.Overflow;
            watermarkText.raycastTarget = false;
            watermarkText.color = new Color(0.85f, 0.98f, 1f, 0.95f);
            watermarkText.text = watermark;
            // Two outline rings: a tight bright one and a wide soft one, read together as a halo.
            foreach (float spread in new[] { 1.6f, 3.6f })
            {
                var glow = mark.AddComponent<Outline>();
                glow.effectDistance = new Vector2(spread, -spread);
                glow.useGraphicAlpha = true;
                watermarkGlow.Add(glow);
            }
        }

        /// <summary>
        /// The game auto-rotates. The reference resolution follows the orientation so the short screen edge is
        /// always 1080 units: the same layout then fits a portrait phone and a landscape one.
        /// </summary>
        void FitOrientation()
        {
            if (scaler == null) return;
            bool nowLandscape = Screen.width > Screen.height;
            if (nowLandscape == landscape) return;
            landscape = nowLandscape;
            scaler.referenceResolution = landscape ? new Vector2(1920f, 1080f) : new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = landscape ? 1f : 0f;
        }

        void BuildReticle(Transform parent, Vector2 frame)
        {
            const float length = 80f, thickness = 6f;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                Vector2 corner = new(sx * frame.x * 0.5f, sy * frame.y * 0.5f);
                reticle.Add(Bar(parent, "H", corner + new Vector2(-sx * length * 0.5f, 0f), new Vector2(length, thickness), accent));
                reticle.Add(Bar(parent, "V", corner + new Vector2(0f, -sy * length * 0.5f), new Vector2(thickness, length), accent));
            }
        }

        static CanvasGroup Group(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            var group = go.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
            return group;
        }

        Text Label(Transform parent, string name, int size, FontStyle style, Vector2 position, Color color)
        {
            var text = new GameObject(name, typeof(RectTransform)).AddComponent<Text>();
            Place(text.rectTransform, parent, position, new Vector2(1000f, size * 1.6f));
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static Image Bar(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var image = new GameObject(name, typeof(RectTransform)).AddComponent<Image>();
            Place(image.rectTransform, parent, position, size);
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static void Place(RectTransform rect, Transform parent, Vector2 position, Vector2 size)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        static Sprite s_White;

        // A filled Image needs a sprite to fill; the default white one is not exposed.
        static Sprite WhiteSprite()
        {
            if (s_White != null) return s_White;
            var tex = Texture2D.whiteTexture;
            s_White = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return s_White;
        }

        static string Spaced(string s) => string.IsNullOrEmpty(s) ? string.Empty : string.Join(" ", s.ToCharArray());

        static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, a);
    }
}
