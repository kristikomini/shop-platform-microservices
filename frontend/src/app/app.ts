import { Component, OnInit, OnDestroy, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ApiService } from './api.service';
import { Product, Order } from './models';

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private pollHandle?: ReturnType<typeof setInterval>;

  products = signal<Product[]>([]);
  orders = signal<Order[]>([]);
  qty = signal<Record<number, number>>({});
  loadingProducts = signal(true);
  loadingOrders = signal(true);
  placing = signal<number | null>(null);
  toast = signal<{ text: string; ok: boolean } | null>(null);

  search = signal('');
  creating = signal(false);
  npName = signal('');
  npPrice = signal<number | null>(null);
  npStock = signal<number | null>(null);

  // Auth state
  username = signal<string | null>(null);
  role = signal<string | null>(null);
  loginUsername = signal('');
  loginPassword = signal('');
  loggingIn = signal(false);
  isLoggedIn = computed(() => this.username() !== null);
  isAdmin = computed(() => this.role() === 'Admin');

  ngOnInit(): void {
    this.restoreSession();
    this.loadProducts();
    this.loadOrders();
    // Poll orders so status transitions (Placed -> Shipped) appear live.
    this.pollHandle = setInterval(() => this.loadOrders(true), 3000);
  }

  ngOnDestroy(): void {
    if (this.pollHandle) clearInterval(this.pollHandle);
  }

  private restoreSession(): void {
    try {
      const u = localStorage.getItem('username');
      const r = localStorage.getItem('role');
      if (u && localStorage.getItem('token')) {
        this.username.set(u);
        this.role.set(r);
      }
    } catch { /* private mode / blocked storage */ }
  }

  login(): void {
    const u = this.loginUsername().trim();
    const p = this.loginPassword();
    if (!u || !p) { this.showToast('Enter username and password', false); return; }
    this.loggingIn.set(true);
    this.api.login(u, p).subscribe({
      next: (res) => {
        try {
          localStorage.setItem('token', res.token);
          localStorage.setItem('username', res.username);
          localStorage.setItem('role', res.role);
        } catch { /* ignore */ }
        this.username.set(res.username);
        this.role.set(res.role);
        this.loginPassword.set('');
        this.loggingIn.set(false);
        this.showToast(`Signed in as ${res.username}`, true);
      },
      error: () => {
        this.loggingIn.set(false);
        this.showToast('Invalid username or password', false);
      }
    });
  }

  logout(): void {
    try {
      localStorage.removeItem('token');
      localStorage.removeItem('username');
      localStorage.removeItem('role');
    } catch { /* ignore */ }
    this.username.set(null);
    this.role.set(null);
    this.showToast('Signed out', true);
  }

  loadProducts(): void {
    this.loadingProducts.set(true);
    this.api.getProducts(this.search()).subscribe({
      next: (p) => {
        this.products.set(p);
        const q: Record<number, number> = { ...this.qty() };
        p.forEach((x) => (q[x.id] ??= 1));
        this.qty.set(q);
        this.loadingProducts.set(false);
      },
      error: () => {
        this.showToast('Could not reach the Catalog service', false);
        this.loadingProducts.set(false);
      }
    });
  }

  // `silent` is used by the poller so it doesn't flash a spinner every 3s.
  loadOrders(silent = false): void {
    if (!silent) this.loadingOrders.set(true);
    this.api.getOrders().subscribe({
      next: (o) => {
        this.orders.set(o);
        this.loadingOrders.set(false);
      },
      error: () => {
        if (!silent) this.showToast('Could not reach the Orders service', false);
        this.loadingOrders.set(false);
      }
    });
  }

  onSearch(term: string): void {
    this.search.set(term);
    this.loadProducts();
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
        this.loadProducts(); // reflect the decremented stock
      },
      error: (err: HttpErrorResponse) => {
        const msg = err.status === 401
          ? 'Please sign in to place an order.'
          : (typeof err.error === 'string' && err.error ? err.error : 'Order failed');
        this.showToast(msg, false);
        this.placing.set(null);
        this.loadProducts(); // stock may have changed elsewhere
      }
    });
  }

  addProduct(): void {
    const name = this.npName().trim();
    const price = this.npPrice();
    const stock = this.npStock();
    if (!name || price === null || price < 0 || stock === null || stock < 0) {
      this.showToast('Fill in name, price and stock', false);
      return;
    }
    this.creating.set(true);
    this.api.addProduct({ name, price, stock }).subscribe({
      next: (p) => {
        this.showToast(`Added ${p.name}`, true);
        this.npName.set('');
        this.npPrice.set(null);
        this.npStock.set(null);
        this.creating.set(false);
        this.loadProducts();
      },
      error: (err: HttpErrorResponse) => {
        const msg = err.status === 401 ? 'Please sign in as an admin.'
          : err.status === 403 ? 'Admin role required to add products.'
          : 'Could not add product';
        this.showToast(msg, false);
        this.creating.set(false);
      }
    });
  }

  private showToast(text: string, ok: boolean): void {
    this.toast.set({ text, ok });
    setTimeout(() => this.toast.set(null), 3500);
  }
}
