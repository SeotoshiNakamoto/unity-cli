using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal struct ClickResult
    {
        public bool Success;
        public string Strategy;  // "click_event", "delegate_invoke", "pointer_events"
        public string Error;
    }

    internal static class ClickExecutor
    {
        const BindingFlags kNonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static ClickResult Execute(VisualElement target)
        {
            if (target == null)
                return new ClickResult { Success = false, Error = "Target element is null" };

            // Strategy 1: ClickEvent synthesis (covers RegisterCallback<ClickEvent> AND Button.clicked)
            var result = TryClickEvent(target);
            if (result.Success)
                return result;

            // Strategy 2: Delegate invoke (Button.clickable.clicked direct call)
            result = TryDelegateInvoke(target);
            if (result.Success)
                return result;

            // Strategy 3: PointerDown/Up synthesis
            result = TryPointerEvents(target);
            return result;
        }

        static ClickResult TryClickEvent(VisualElement target)
        {
            try
            {
                var panel = target.panel;
                if (panel == null)
                    return new ClickResult { Success = false, Error = "Element has no panel" };

                using (var evt = ClickEvent.GetPooled())
                {
                    evt.target = target;
                    target.SendEvent(evt);
                }
                return new ClickResult { Success = true, Strategy = "click_event" };
            }
            catch (Exception e)
            {
                return new ClickResult { Success = false, Error = $"ClickEvent failed: {e.Message}" };
            }
        }

        static ClickResult TryDelegateInvoke(VisualElement target)
        {
            if (!(target is Button button))
                return new ClickResult { Success = false, Error = "Not a Button" };

            var clickable = button.clickable;
            if (clickable == null)
                return new ClickResult { Success = false, Error = "No clickable" };

            try
            {
                var clickedField = typeof(Clickable).GetField("m_Clicked", kNonPublicInstance);
                if (clickedField == null)
                    clickedField = typeof(Clickable).GetField("clicked", kNonPublicInstance);

                if (clickedField != null)
                {
                    var action = clickedField.GetValue(clickable) as Action;
                    if (action != null)
                    {
                        action.Invoke();
                        return new ClickResult { Success = true, Strategy = "delegate_invoke" };
                    }
                }

                return new ClickResult { Success = false, Error = "No invocable delegate found" };
            }
            catch (Exception e)
            {
                return new ClickResult { Success = false, Error = $"Delegate invoke failed: {e.Message}" };
            }
        }

        static ClickResult TryPointerEvents(VisualElement target)
        {
            try
            {
                var panel = target.panel;
                if (panel == null)
                    return new ClickResult { Success = false, Error = "Element has no panel" };

                var wb = target.worldBound;
                var center = wb.center;
                var screenCenter = new Vector3(center.x, center.y, 0);

                SendPointerEvent<PointerDownEvent>(target, screenCenter);
                SendPointerEvent<PointerUpEvent>(target, screenCenter);

                return new ClickResult { Success = true, Strategy = "pointer_events" };
            }
            catch (Exception e)
            {
                return new ClickResult { Success = false, Error = $"PointerEvents failed: {e.Message}" };
            }
        }

        static void SendPointerEvent<T>(VisualElement target, Vector3 position) where T : PointerEventBase<T>, new()
        {
            using (var evt = PointerEventBase<T>.GetPooled())
            {
                evt.target = target;

                var baseType = typeof(PointerEventBase<T>);
                SetField(baseType, evt, "m_LocalPosition", position);
                SetField(baseType, evt, "m_Position", position);

                target.SendEvent(evt);
            }
        }

        static void SetField(Type type, object obj, string fieldName, object value)
        {
            var field = type.GetField(fieldName, kNonPublicInstance);
            field?.SetValue(obj, value);
        }
    }
}
