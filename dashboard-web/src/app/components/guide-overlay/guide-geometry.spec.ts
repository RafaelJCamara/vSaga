import { Box, Size, placePopover, spotlightBox } from './guide-geometry';

const viewport: Size = { width: 1000, height: 700 };
const popover: Size = { width: 300, height: 160 };

describe('spotlightBox', () => {
  it('grows the element by the padding on every side', () => {
    const box = spotlightBox({ top: 100, left: 200, width: 300, height: 50 }, viewport, 6);

    expect(box).toEqual({ top: 94, left: 194, width: 312, height: 62 });
  });

  it('pads by 6 pixels unless told otherwise', () => {
    expect(spotlightBox({ top: 100, left: 100, width: 10, height: 10 }, viewport)).toEqual({
      top: 94,
      left: 94,
      width: 22,
      height: 22,
    });
  });

  it('clips the grown box to the viewport on each edge', () => {
    // Overhangs the top-left corner: the padding and the part off screen are both cut.
    expect(spotlightBox({ top: -20, left: 3, width: 100, height: 60 }, viewport, 6)).toEqual({
      top: 0,
      left: 0,
      width: 109,
      height: 46,
    });
    // Overhangs the bottom-right corner.
    expect(spotlightBox({ top: 650, left: 950, width: 100, height: 100 }, viewport, 6)).toEqual({
      top: 644,
      left: 944,
      width: 56,
      height: 56,
    });
  });

  it('is null when nothing of the element is on screen', () => {
    const size = { width: 100, height: 100 };
    expect(spotlightBox({ ...size, top: -100, left: 10 }, viewport)).toBeNull(); // ends at the top edge
    expect(spotlightBox({ ...size, top: 10, left: -150 }, viewport)).toBeNull();
    expect(spotlightBox({ ...size, top: 10, left: -100 }, viewport)).toBeNull(); // ends at the left edge
    expect(spotlightBox({ ...size, top: 700, left: 10 }, viewport)).toBeNull(); // starts at the bottom edge
    expect(spotlightBox({ ...size, top: 10, left: 1000 }, viewport)).toBeNull();
  });

  it('keeps an element that is only just on screen, however thin the sliver', () => {
    const box = spotlightBox({ top: -99, left: 10, width: 100, height: 100 }, viewport, 6);

    expect(box).not.toBeNull();
    expect(box?.top).toBe(0);
    expect(box?.height).toBe(7);
  });

  it('is null for an element with no area (hidden)', () => {
    expect(spotlightBox({ top: 10, left: 10, width: 0, height: 40 }, viewport)).toBeNull();
    expect(spotlightBox({ top: 10, left: 10, width: 40, height: 0 }, viewport)).toBeNull();
  });
});

describe('placePopover', () => {
  const spot: Box = { top: 300, left: 400, width: 200, height: 60 };

  it('puts the popover below the spot by default, centred on it, a gap away', () => {
    const place = placePopover(spot, popover, viewport);

    expect(place).toEqual({ top: 372, left: 350, placement: 'bottom' });
  });

  it('puts it on the preferred side when there is room', () => {
    expect(placePopover(spot, popover, viewport, 'top')).toEqual({
      top: 128,
      left: 350,
      placement: 'top',
    });
    expect(placePopover(spot, popover, viewport, 'right')).toEqual({
      top: 250,
      left: 612,
      placement: 'right',
    });
    expect(placePopover(spot, popover, viewport, 'left')).toEqual({
      top: 250,
      left: 88,
      placement: 'left',
    });
  });

  it('takes the opposite side when the preferred one does not fit', () => {
    const low: Box = { top: 560, left: 400, width: 200, height: 60 }; // 12 + 160 below it does not fit

    expect(placePopover(low, popover, viewport, 'bottom').placement).toBe('top');
    const high: Box = { top: 20, left: 400, width: 200, height: 60 };
    expect(placePopover(high, popover, viewport, 'top').placement).toBe('bottom');
  });

  it('tries the other axis next, then the remaining side, in that order', () => {
    // A tall element across the middle: nothing above or below, room only to its right.
    const tall: Box = { top: 20, left: 100, width: 200, height: 660 };
    expect(placePopover(tall, popover, viewport, 'bottom')).toMatchObject({
      placement: 'right',
      left: 312,
    });

    // The same element against the right edge: right does not fit, left does.
    const tallRight: Box = { top: 20, left: 700, width: 280, height: 660 };
    expect(placePopover(tallRight, popover, viewport, 'bottom').placement).toBe('left');

    // Preferring a side axis falls back to the vertical ones.
    const wide: Box = { top: 300, left: 20, width: 960, height: 60 };
    expect(placePopover(wide, popover, viewport, 'right').placement).toBe('bottom');
  });

  it('keeps the popover inside the margin on the cross axis', () => {
    const atLeft: Box = { top: 300, left: 0, width: 60, height: 60 };
    expect(placePopover(atLeft, popover, viewport, 'bottom').left).toBe(12);

    const atRight: Box = { top: 300, left: 940, width: 60, height: 60 };
    expect(placePopover(atRight, popover, viewport, 'bottom').left).toBe(1000 - 300 - 12);

    const nearTop: Box = { top: 0, left: 800, width: 60, height: 20 };
    expect(placePopover(nearTop, popover, viewport, 'right', 12, 12)).toMatchObject({
      placement: 'left',
      top: 12,
    });
  });

  it('goes to the bottom-right corner, over the element, when no side has room', () => {
    // The table fills the viewport.
    const fills: Box = { top: 0, left: 0, width: 1000, height: 700 };

    expect(placePopover(fills, popover, viewport)).toEqual({
      top: 700 - 160 - 12,
      left: 1000 - 300 - 12,
      placement: 'inside',
    });
  });

  it('is centred when nothing is highlighted', () => {
    expect(placePopover(null, popover, viewport)).toEqual({
      top: 270,
      left: 350,
      placement: 'center',
    });
  });

  it('honours the gap and the margin it is given', () => {
    const place = placePopover(spot, popover, viewport, 'bottom', 30, 12);
    expect(place.top).toBe(300 + 60 + 30);

    // The margin is room kept clear of the edge: at 200 no side has enough left, so the corner is used.
    expect(placePopover(spot, popover, viewport, 'bottom', 12, 200).placement).toBe('inside');
  });

  it('never leaves the corner of a viewport smaller than the popover', () => {
    const small: Size = { width: 200, height: 100 };

    const place = placePopover({ top: 0, left: 0, width: 200, height: 100 }, popover, small);
    expect(place.placement).toBe('inside');
    expect(place.top).toBeGreaterThanOrEqual(12);
    expect(place.left).toBeGreaterThanOrEqual(12);
  });
});
