import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SagaKind } from '../../models/saga.model';

@Component({
  selector: 'app-kind-badge',
  imports: [],
  templateUrl: './kind-badge.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './kind-badge.scss',
})
export class KindBadge {
  readonly kind = input.required<SagaKind>();
}
