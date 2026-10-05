import { GuidePlacement } from '../../models/guide.model';

/** A rectangle in viewport coordinates, as `getBoundingClientRect` gives it (without `right` and `bottom`). */
export interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

export interface Size {
  width: number;
  height: number;
}

/** Where a popover goes and which side of the highlighted element that is: `inside` when no side has room
 *  (an element that fills the viewport: the popover sits in its bottom-right corner), `center` when
 *  nothing is highlighted. */
export interface PopoverPlacement {
  top: number;
  left: number;
  placement: GuidePlacement | 'inside' | 'center';
}

/** The order sides are tried in: the preferred one, its opposite, then the other two. */
const SIDES: Record<GuidePlacement, GuidePlacement[]> = {
  bottom: ['bottom', 'top', 'right', 'left'],
  top: ['top', 'bottom', 'right', 'left'],
  right: ['right', 'left', 'bottom', 'top'],
  left: ['left', 'right', 'bottom', 'top'],
};

/**
 * The highlighted area for an element: its box grown by `pad` on every side and clipped to the viewport,
 * or null when nothing of the element is on screen (or it has no area: a hidden element). Pure.
 */
export function spotlightBox(anchor: Box, viewport: Size, pad = 6): Box | null {
  if (anchor.width <= 0 || anchor.height <= 0) return null;
  const right = anchor.left + anchor.width;
  const bottom = anchor.top + anchor.height;
  if (right <= 0 || bottom <= 0 || anchor.left >= viewport.width || anchor.top >= viewport.height) {
    return null;
  }
  const left = Math.max(0, anchor.left - pad);
  const top = Math.max(0, anchor.top - pad);
  return {
    top,
    left,
    width: Math.min(viewport.width, right + pad) - left,
    height: Math.min(viewport.height, bottom + pad) - top,
  };
}

/**
 * Whether `anchor` is on screen well enough to need no scrolling: on each axis it starts inside the viewport and
 * ends inside it too, or is bigger than the viewport there (its start is then what shows). Pure.
 */
export function isInView(anchor: Box, viewport: Size): boolean {
  return (
    fits(anchor.top, anchor.height, viewport.height) &&
    fits(anchor.left, anchor.width, viewport.width)
  );
}

/** One axis of `isInView`. */
function fits(start: number, length: number, extent: number): boolean {
  return start >= 0 && start < extent && (start + length <= extent || length > extent);
}

/**
 * Where a popover of size `popover` goes beside the highlighted `spot`. It tries `preferred`, then that
 * side's opposite, then the other two, and takes the first that fits inside `margin` of the viewport edge;
 * across that axis it is centred on the spot and kept inside the margin. When no side fits it goes to the
 * bottom-right corner, over the element (`inside`); without a spot it is centred (`center`). Pure.
 */
export function placePopover(
  spot: Box | null,
  popover: Size,
  viewport: Size,
  preferred: GuidePlacement = 'bottom',
  gap = 12,
  margin = 12,
): PopoverPlacement {
  const clampX = (x: number) => clamp(x, margin, viewport.width - popover.width - margin);
  const clampY = (y: number) => clamp(y, margin, viewport.height - popover.height - margin);

  if (spot === null) {
    return {
      top: Math.max(0, (viewport.height - popover.height) / 2),
      left: Math.max(0, (viewport.width - popover.width) / 2),
      placement: 'center',
    };
  }

  const centreX = spot.left + spot.width / 2 - popover.width / 2;
  const centreY = spot.top + spot.height / 2 - popover.height / 2;
  for (const side of SIDES[preferred]) {
    switch (side) {
      case 'bottom': {
        const top = spot.top + spot.height + gap;
        if (top + popover.height <= viewport.height - margin) {
          return { top, left: clampX(centreX), placement: side };
        }
        break;
      }
      case 'top': {
        const top = spot.top - gap - popover.height;
        if (top >= margin) return { top, left: clampX(centreX), placement: side };
        break;
      }
      case 'right': {
        const left = spot.left + spot.width + gap;
        if (left + popover.width <= viewport.width - margin) {
          return { top: clampY(centreY), left, placement: side };
        }
        break;
      }
      case 'left': {
        const left = spot.left - gap - popover.width;
        if (left >= margin) return { top: clampY(centreY), left, placement: side };
        break;
      }
    }
  }
  return {
    top: Math.max(margin, viewport.height - popover.height - margin),
    left: Math.max(margin, viewport.width - popover.width - margin),
    placement: 'inside',
  };
}

/** `value` held between `min` and `max`; when the range is empty (a popover wider than the viewport) `min` wins. */
function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(value, max));
}
