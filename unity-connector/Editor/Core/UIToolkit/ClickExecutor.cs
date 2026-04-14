using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCliConnector.UIToolkit
{
    internal struct ClickResult
    {
        public bool Success;
        public string Strategy;  // "delegate_invoke", "click_event", "pointer_events"
        public string Error;
    }

    internal static class ClickExecutor
    {
        const BindingFlags kNonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static ClickResult Execute(VisualElement target)
        {
            if (target == null)
                return new ClickResult { Success = false, Error = "Target element is null" };

            // Strategy 1: Delegate invoke (Button.clickable.clicked)
            var result = TryDelegateInvoke(target);
            if (result.Success)
                return result;

            // Strategy 2: ClickEvent synthesis via panel
            result = TryClickEvent(target);
            if (result.Success)
                return result;

            // Strategy 3: PointerDown/Up synthesis
            result = TryPointerEvents(target);
            return result;
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
                // Access the private m_Clicked field (backing field for clicked event)
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

                // Fallback: try Clickable.Invoke (internal method in some Unity versions)
                var invokeMethod = typeof(Clickable).GetMethod("Invoke", kNonPublicInstance);
                if (invokeMethod != null)
                {
                    invokeMethod.Invoke(clickable, new object[] { null });
                    return new ClickResult { Success = true, Strategy = "delegate_invoke" };
                }

                return new ClickResult { Success = false, Error = "No invocable delegate found" };
            }
            catch (Exception e)
            {
                return new ClickResult { Success = false, Error = $"Delegate invoke failed: {e.Message}" };
            }
        }

        static ClickResult TryClickEvent(VisualElement target)
        {
            try
            {
                var panel = target.panel;
                if (panel == null)
                    return new ClickResult { Success = false, Error = "Element has no panel" };

                var wb = target.worldBound;
                var pos = wb.center;

                // Use NavigationSubmitEvent as a more reliable alternative for triggering click callbacks,
                // then fall back to using reflection to create and dispatch a ClickEvent.
                // Direct ClickEvent.GetPooled() is unreliable across Unity versions.
                using (var evt = NavigationSubmitEvent.GetPooled())
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

                // PointerDownEvent/PointerUpEvent: use reflection to set position fields
                // since the public API for GetPooled with position varies by Unity version.
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

                // Set position via reflection — these are writable internal properties
                var baseType = typeof(PointerEventBase<T>);
                SetField(baseType, evt, "m_LocalPosition", position);
                SetField(baseType, evt, "m_Position", position);

                // Also try the EventBase position fields
                var eventBaseType = typeof(EventBase);
                SetField(eventBaseType, evt, "m_Position", position);

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
