import { TestBed } from '@angular/core/testing';
import { KindBadge } from './kind-badge';

describe('KindBadge', () => {
  it('renders the solid variant for Orchestrated with a "drives" tooltip', () => {
    const fixture = TestBed.createComponent(KindBadge);
    fixture.componentRef.setInput('kind', 'Orchestrated');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const badge = el.querySelector('.kind-badge');

    expect(el.textContent).toContain('Orchestrated');
    expect(badge?.classList.contains('kind-badge--choreographed')).toBe(false);
    expect(badge?.getAttribute('title')).toContain('drives');
  });

  it('renders the outline variant for Choreographed with an "observes" tooltip', () => {
    const fixture = TestBed.createComponent(KindBadge);
    fixture.componentRef.setInput('kind', 'Choreographed');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const badge = el.querySelector('.kind-badge');

    expect(el.textContent).toContain('Choreographed');
    expect(badge?.classList.contains('kind-badge--choreographed')).toBe(true);
    expect(badge?.getAttribute('title')).toContain('observes');
  });
});
