import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConfirmButton } from './confirm-button';

/** A page that uses the button the way the administration pages do. */
@Component({
  selector: 'app-host',
  imports: [ConfirmButton],
  template: `
    <button id="before" type="button">before</button>
    <app-confirm-button
      label="Delete role"
      [prompt]="prompt()"
      confirmLabel="Yes, delete"
      [busy]="busy()"
      [disabled]="disabled()"
      (confirmed)="confirmed.set(confirmed() + 1)"
    />
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class Host {
  readonly prompt = signal('Delete the role Support?');
  readonly busy = signal(false);
  readonly disabled = signal(false);
  readonly confirmed = signal(0);
}

describe('ConfirmButton', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  beforeEach(() => {
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    // Attached to the document: focus is only real there.
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
  });

  afterEach(() => fixture.nativeElement.remove());

  const root = () => fixture.nativeElement as HTMLElement;
  const buttons = () =>
    Array.from(root().querySelectorAll<HTMLButtonElement>('app-confirm-button button'));
  const labels = () => buttons().map((b) => b.textContent?.trim());
  const ask = () => root().querySelector<HTMLButtonElement>('app-confirm-button button')!;
  const prompt = () => root().querySelector('.confirm-prompt');
  const focused = () => document.activeElement;
  const find = (label: string) => buttons().find((b) => b.textContent?.trim() === label)!;

  async function render(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(): Promise<void> {
    ask().click();
    await render();
  }

  function key(target: Element, name: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true });
    target.dispatchEvent(event);
    return event;
  }

  it('shows one button and no question until it is clicked', () => {
    expect(labels()).toEqual(['Delete role']);
    expect(prompt()).toBeNull();
    expect(host.confirmed()).toBe(0);
  });

  it('asks the question with a yes and a Cancel, and does nothing yet', async () => {
    await open();

    expect(prompt()?.textContent).toBe('Delete the role Support?');
    expect(labels()).toEqual(['Yes, delete', 'Cancel']);
    expect(host.confirmed()).toBe(0);
  });

  it('describes both buttons by the question, so a screen reader reads it with the focused one', async () => {
    await open();

    const id = prompt()!.id;
    expect(id).not.toBe('');
    expect(find('Yes, delete').getAttribute('aria-describedby')).toBe(id);
    expect(find('Cancel').getAttribute('aria-describedby')).toBe(id);
  });

  it('gives every instance a question of its own', async () => {
    const second = TestBed.createComponent(ConfirmButton);
    second.componentRef.setInput('label', 'Delete');
    second.componentRef.setInput('prompt', 'Sure?');
    second.componentRef.setInput('confirmLabel', 'Yes');
    document.body.appendChild(second.nativeElement);
    second.detectChanges();
    second.nativeElement.querySelector('button').click();
    second.detectChanges();
    await open();

    const ids = [prompt()!.id, second.nativeElement.querySelector('.confirm-prompt').id];
    expect(new Set(ids).size).toBe(2);
    second.nativeElement.remove();
  });

  describe('the keyboard', () => {
    it('moves focus to Cancel when the question opens, not to the action', async () => {
      ask().focus();
      await open();

      expect(focused()).toBe(find('Cancel'));
    });

    it('cancels with Escape, and returns focus to the button that asked', async () => {
      ask().focus();
      await open();

      key(focused() as Element, 'Escape');
      await render();

      expect(prompt()).toBeNull();
      expect(labels()).toEqual(['Delete role']);
      expect(focused()).toBe(ask());
      expect(host.confirmed()).toBe(0);
    });

    it('cancels with Escape from the yes button too', async () => {
      await open();
      find('Yes, delete').focus();

      key(find('Yes, delete'), 'Escape');
      await render();

      expect(prompt()).toBeNull();
      expect(host.confirmed()).toBe(0);
    });

    it('keeps Escape to itself: nothing behind the question hears it', async () => {
      const behind = vi.fn();
      document.addEventListener('keydown', behind);
      await open();

      key(find('Cancel'), 'Escape');
      document.removeEventListener('keydown', behind);

      expect(behind).not.toHaveBeenCalled();
    });

    it('leaves Escape alone while no question is open', () => {
      const behind = vi.fn();
      document.addEventListener('keydown', behind);

      key(ask(), 'Escape');
      document.removeEventListener('keydown', behind);

      expect(behind).toHaveBeenCalledTimes(1);
    });

    it('is reachable and operable with the keyboard alone: Tab order is the question, yes, Cancel', async () => {
      await open();

      // The buttons are plain buttons in document order: nothing has a tabindex that would reorder them.
      expect(buttons().map((b) => b.getAttribute('tabindex'))).toEqual([null, null]);
      expect(labels()).toEqual(['Yes, delete', 'Cancel']);
    });
  });

  describe('Cancel', () => {
    it('closes the question, emits nothing and returns focus to the button', async () => {
      await open();

      find('Cancel').click();
      await render();

      expect(labels()).toEqual(['Delete role']);
      expect(host.confirmed()).toBe(0);
      expect(focused()).toBe(ask());
    });
  });

  describe('the yes button', () => {
    it('emits confirmed once and closes the question', async () => {
      await open();

      find('Yes, delete').click();
      await render();

      expect(host.confirmed()).toBe(1);
      expect(labels()).toEqual(['Delete role']);
    });

    it('asks again for the next one: a confirmation is not remembered', async () => {
      await open();
      find('Yes, delete').click();
      await render();

      await open();
      expect(labels()).toEqual(['Yes, delete', 'Cancel']);
      expect(host.confirmed()).toBe(1);
    });

    it('takes focus back to the first button when it was lost with the button that held it', async () => {
      await open();
      find('Yes, delete').focus();

      find('Yes, delete').click();
      await render();

      expect(focused()).toBe(ask());
    });

    it('leaves focus where the user moved it while the work runs', async () => {
      await open();
      find('Yes, delete').click();
      document.getElementById('before')!.focus();
      await render();

      expect(focused()).toBe(document.getElementById('before'));
    });
  });

  describe('busy', () => {
    it('keeps the button from asking again, without dropping focus', async () => {
      ask().focus();
      host.busy.set(true);
      await render();

      expect(ask().getAttribute('aria-disabled')).toBe('true');
      // Not `disabled`: a disabled button would drop the focus it holds.
      expect(ask().disabled).toBe(false);
      expect(focused()).toBe(ask());

      ask().click();
      await render();
      expect(prompt()).toBeNull();
    });

    it('asks again once the work is over', async () => {
      host.busy.set(true);
      await render();
      host.busy.set(false);
      await render();

      expect(ask().getAttribute('aria-disabled')).toBeNull();
      await open();
      expect(prompt()).not.toBeNull();
    });
  });

  describe('disabled', () => {
    it('disables the button and asks nothing', async () => {
      host.disabled.set(true);
      await render();

      expect(ask().disabled).toBe(true);
      ask().click();
      await render();
      expect(prompt()).toBeNull();
      expect(host.confirmed()).toBe(0);
    });

    it('closes a question that is open, and does not bring it back when the button is enabled again', async () => {
      await open();

      host.disabled.set(true);
      await render();
      expect(prompt()).toBeNull();
      expect(labels()).toEqual(['Delete role']);

      host.disabled.set(false);
      await render();
      expect(prompt()).toBeNull();
      expect(labels()).toEqual(['Delete role']);
    });
  });

  it('shows the question the page gives it, as it changes', async () => {
    await open();

    host.prompt.set('Delete the role Billing?');
    await render();

    expect(prompt()?.textContent).toBe('Delete the role Billing?');
  });
});
