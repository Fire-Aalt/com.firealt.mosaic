using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class IntGridMatrixManipulator : PointerManipulator
    {
        public enum Pressed
        {
            None,
            LeftMouseButton,
            RightMouseButton,
        }
        
        private bool _active;
        private int _pointerId;
        private Pressed _pressedButton;
        
        public Action<VisualElement> HoverEnter;
        public Action<VisualElement> HoverLeave;
        public Action<VisualElement, Pressed> DragEnter;
        public Action DragStop;

        private readonly HashSet<VisualElement> _visitedSet = new();
        
        public IntGridMatrixManipulator()
        {
            _pointerId = -1;
            activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
            activators.Add(new ManipulatorActivationFilter { button = MouseButton.RightMouse });
            _active = false;
        }

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerEnterEvent>(OnPointerEnter, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerLeaveEvent>(OnPointerLeave, TrickleDown.TrickleDown);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            target.UnregisterCallback<PointerEnterEvent>(OnPointerEnter);
            target.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
        }
        
        private void OnPointerDown(PointerDownEvent e)
        {
            if (_active)
            {
                e.StopImmediatePropagation();
                return;
            }

            if (CanStartManipulation(e))
            {
                _pointerId = e.pointerId;
                _pressedButton = e.button switch
                {
                    0 => Pressed.LeftMouseButton,
                    1 => Pressed.RightMouseButton,
                    _ => Pressed.None,
                };
                
                _active = true;
                target.CapturePointer(_pointerId);
                
                TryEnter(e.target);
                
                e.StopPropagation();
            }
        }
        
        private void OnPointerUp(PointerUpEvent e)
        {
            if (!_active || !target.HasPointerCapture(_pointerId) || !CanStopManipulation(e))
                return;

            _visitedSet.Clear();
            _pressedButton = Pressed.None;
            _active = false;
            target.ReleaseMouse();
            e.StopPropagation();
            DragStop?.Invoke();
        }
        
        private void OnPointerEnter(PointerEnterEvent e)
        {
            TryEnter(e.target);

            e.StopPropagation();
        }

        private void TryEnter(IEventHandler eTarget)
        {
            var selected = FindCellTarget(eTarget as VisualElement);
            if (selected == null) return;
            
            if (_active)
            {
                if (!_visitedSet.Contains(selected))
                {
                    DragEnter?.Invoke(selected, _pressedButton);
                    _visitedSet.Add(selected);
                }
            }
            HoverEnter?.Invoke(selected);
        }

        private void OnPointerLeave(PointerLeaveEvent e)
        {
            var deselected = FindCellTarget(e.target as VisualElement);
            if (deselected == null) return;
            
            HoverLeave?.Invoke(deselected);
            
            e.StopPropagation();
        }
        
        private static VisualElement FindCellTarget(VisualElement ve)
        {
            for (var cur = ve; cur != null; cur = cur.hierarchy.parent)
            {
                if (cur.ClassListContains("int-grid-matrix-cell"))
                    return cur;
            }
            return null;
        }
    }
}