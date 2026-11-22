using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HideoutQoL.Client.UI;

/// <summary>Shared timings for Workshop transitions.</summary>
internal static class WorkshopFx
{
    internal const float WindowFadeIn = 0.16f;
    internal const float WindowFadeOut = 0.10f;
    internal const float ContentFade = 0.14f;
    internal const float PreviewFade = 0.22f;
    internal const float HoverSeconds = 0.10f;

    /// <summary>
    /// Longest frame a transition will account for. The frame that starts a fade usually also did the heavy
    /// work (list rebuild, item views, model load); without this clamp its long delta would finish the fade
    /// in a single step and the content would pop in exactly as before.
    /// </summary>
    internal const float MaxStep = 1f / 30f;

    /// <summary>User toggle for every fade / tint transition. Loading stays time-sliced either way.</summary>
    internal static bool Animate =>
        HideoutQoLPlugin.WorkshopAnimations == null || HideoutQoLPlugin.WorkshopAnimations.Value;

    internal static void SetColor(Graphic graphic, Color color, bool animate)
    {
        if (graphic == null)
        {
            return;
        }

        graphic.DOKill();
        if (animate && Animate && graphic.gameObject.activeInHierarchy)
        {
            graphic.DOColor(color, HoverSeconds).SetUpdate(true);
        }
        else
        {
            graphic.color = color;
        }
    }
}

/// <summary>
/// CanvasGroup alpha fade that only ticks while a fade is running and never advances by more than
/// <see cref="WorkshopFx.MaxStep"/> per frame.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
internal sealed class UiFader : MonoBehaviour
{
    private CanvasGroup _group;
    private float _target = 1f;
    private float _speed;
    private Action _onDone;

    internal static UiFader For(GameObject go)
    {
        UiFader fader = go.GetComponent<UiFader>();
        if (fader == null)
        {
            if (go.GetComponent<CanvasGroup>() == null)
            {
                go.AddComponent<CanvasGroup>();
            }
            fader = go.AddComponent<UiFader>();
            fader._group = go.GetComponent<CanvasGroup>();
            fader.enabled = false;
        }
        return fader;
    }

    internal float Alpha => _group != null ? _group.alpha : 1f;

    internal void Play(float from, float to, float duration, Action onDone = null)
    {
        _onDone = onDone;
        _target = to;
        if (duration <= 0f || !WorkshopFx.Animate || !gameObject.activeInHierarchy)
        {
            Snap(to);
            onDone?.Invoke();
            return;
        }

        _group.alpha = from;
        _speed = 1f / duration;
        enabled = true;
    }

    /// <summary>Stops any running fade without invoking its completion callback.</summary>
    internal void Snap(float alpha)
    {
        _onDone = null;
        _target = alpha;
        _group.alpha = alpha;
        enabled = false;
    }

    private void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, WorkshopFx.MaxStep);
        float alpha = Mathf.MoveTowards(_group.alpha, _target, _speed * dt);
        _group.alpha = alpha;
        if (!Mathf.Approximately(alpha, _target))
        {
            return;
        }

        enabled = false;
        Action done = _onDone;
        _onDone = null;
        done?.Invoke();
    }

    private void OnDisable()
    {
        // Deactivated mid-fade (parent screen closed): never leave the group half transparent.
        if (_group != null)
        {
            _group.alpha = _target;
        }
    }
}

/// <summary>
/// Replaces the ScrollRect's stepwise wheel jump with an eased glide over the same distance.
/// Must stay enabled: the EventSystem skips disabled behaviours when dispatching scroll events.
/// It has no Update of its own; the per-frame work lives in <see cref="ScrollGlide"/>, which is only
/// enabled while a glide is in progress.
/// </summary>
internal sealed class SmoothWheelScroll : MonoBehaviour, IScrollHandler
{
    private ScrollRect _scrollRect;
    private ScrollGlide _glide;
    private float _unitsPerDelta;

    internal static void Attach(ScrollRect scrollRect)
    {
        if (scrollRect == null || scrollRect.GetComponent<SmoothWheelScroll>() != null)
        {
            return;
        }

        var smooth = scrollRect.gameObject.AddComponent<SmoothWheelScroll>();
        smooth._scrollRect = scrollRect;
        smooth._unitsPerDelta = scrollRect.scrollSensitivity;
        smooth._glide = scrollRect.gameObject.AddComponent<ScrollGlide>();
        smooth._glide.Init(scrollRect);

        // ScrollRect.OnScroll still runs on this GameObject; zero sensitivity turns it into a no-op.
        scrollRect.scrollSensitivity = 0f;
        scrollRect.inertia = false;
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (_scrollRect != null && _scrollRect.content != null)
        {
            _glide.AddDelta(-eventData.scrollDelta.y * _unitsPerDelta);
        }
    }
}

internal sealed class ScrollGlide : MonoBehaviour
{
    private const float Sharpness = 16f;

    private ScrollRect _scrollRect;
    private float _targetY;
    private float _lastAppliedY;

    internal void Init(ScrollRect scrollRect)
    {
        _scrollRect = scrollRect;
        enabled = false;
    }

    internal void AddDelta(float delta)
    {
        RectTransform content = _scrollRect.content;
        float max = MaxScroll(content);
        if (max <= 0f)
        {
            return;
        }

        float current = content.anchoredPosition.y;
        if (!enabled)
        {
            _targetY = current;
        }

        _targetY = Mathf.Clamp(_targetY + delta, 0f, max);
        _lastAppliedY = current;

        if (!WorkshopFx.Animate)
        {
            Apply(content, _targetY);
            enabled = false;
            return;
        }
        enabled = true;
    }

    private void LateUpdate()
    {
        RectTransform content = _scrollRect != null ? _scrollRect.content : null;
        if (content == null)
        {
            enabled = false;
            return;
        }

        if (Mathf.Abs(content.anchoredPosition.y - _lastAppliedY) > 0.5f)
        {
            // Scrollbar drag, scroll reset or clamping moved the content: yield to it.
            enabled = false;
            return;
        }

        _targetY = Mathf.Clamp(_targetY, 0f, Mathf.Max(0f, MaxScroll(content)));
        float dt = Mathf.Min(Time.unscaledDeltaTime, WorkshopFx.MaxStep);
        float next = Mathf.Lerp(content.anchoredPosition.y, _targetY, 1f - Mathf.Exp(-Sharpness * dt));
        if (Mathf.Abs(next - _targetY) < 0.5f)
        {
            next = _targetY;
            enabled = false;
        }
        Apply(content, next);
    }

    private void OnDisable()
    {
        // Reactivated later the content may have moved; start the next glide from wherever it is.
        _lastAppliedY = float.NaN;
    }

    private void Apply(RectTransform content, float y)
    {
        Vector2 pos = content.anchoredPosition;
        pos.y = y;
        content.anchoredPosition = pos;
        _lastAppliedY = y;
    }

    private float MaxScroll(RectTransform content)
    {
        RectTransform viewport = _scrollRect.viewport != null ? _scrollRect.viewport : (RectTransform)_scrollRect.transform;
        return content.rect.height - viewport.rect.height;
    }
}

/// <summary>
/// Idle / hover / selected background for list rows and filter chips, using the vanilla list row colors.
/// Hover eases; selection changes are immediate, as in the handbook.
/// </summary>
internal sealed class HoverTint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    private Image _target;
    private bool _hovered;
    private bool _selected;

    internal static HoverTint Attach(GameObject go, Image target)
    {
        var tint = go.AddComponent<HoverTint>();
        tint._target = target;
        return tint;
    }

    internal void SetSelected(bool selected)
    {
        _selected = selected;
        Apply(animate: false);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _hovered = true;
        Apply(animate: true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovered = false;
        Apply(animate: true);
    }

    private void OnDisable()
    {
        _hovered = false;
        Apply(animate: false);
    }

    private void Apply(bool animate)
    {
        Color color = _selected
            ? EftUiTemplateCache.ColorRowSelected
            : (_hovered ? EftUiTemplateCache.ColorRowHover : EftUiTemplateCache.ColorRowIdle);
        WorkshopFx.SetColor(_target, color, animate && !_selected);
    }
}
