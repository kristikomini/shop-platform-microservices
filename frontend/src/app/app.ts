import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService } from './api.service';
import { Product, Order } from './models';

@Component({
  selector: 'app-root',
  imports: [CommonModule],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App implements OnInit {
  private api = inject(ApiService);

  products = signal<Product[]>([]);
  orders = signal<Order[]>([]);
  qty = signal<Record<number, number>>({});
  loadingProducts = signal(true);
  loadingOrders = signal(true);
  placing = signal<number | null>(null);
  toast = signal<{ text: string; ok: boolean } | null>(null);

  ngOnInit(): void {
    this.loadProducts();
    this.loadOrders();
  }

  loadProducts(): void {
    this.loadingProducts.set(true);
    this.api.getProducts().subscribe({
      next: (p) => {
        this.products.set(p);
        const q: Record<number, number> = {};
        p.forEach((x) => (q[x.id] = 1));
        this.qty.set(q);
        this.loadingProducts.set(false);
      },
      error: () => {
        this.showToast('Could not reach the Catalog service', false);
        this.loadingProducts.set(false);
      }
    });
  }

  loadOrders(): void {
    this.loadingOrders.set(true);
    this.api.getOrders().subscribe({
      next: (o) => {
        this.orders.set(o);
        this.loadingOrders.set(false);
      },
      error: () => {
        this.showToast('Could not reach the Orders service', false);
        this.loadingOrders.set(false);
      }
    });
  }

  setQty(id: number, value: string): void {
    const n = Math.max(1, parseInt(value, 10) || 1);
    this.qty.update((q) => ({ ...q, [id]: n }));
  }

  order(p: Product): void {
    this.placing.set(p.id);
    const quantity = this.qty()[p.id] ?? 1;
    this.api.placeOrder({ productId: p.id, quantity }).subscribe({
      next: (o) => {
        this.showToast(`Ordered ${o.quantity} × ${o.productName} — shipment queued`, true);
        this.placing.set(null);
        this.loadOrders();
      },
      error: () => {
        this.showToast('Order failed', false);
        this.placing.set(null);
      }
    });
  }

  private showToast(text: string, ok: boolean): void {
    this.toast.set({ text, ok });
    setTimeout(() => this.toast.set(null), 3500);
  }
}
