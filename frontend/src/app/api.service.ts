import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Product, Order, CreateOrder, NewProduct, LoginResponse } from './models';

// Every call is a RELATIVE URL. In production the Angular app is served BY the
// gateway, so "/catalog/..." and "/orders/..." hit the gateway on the same
// origin (no CORS). In dev, proxy.conf.json forwards them to the gateway too.
@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  // -> gateway -> Catalog service (optional server-side name search)
  getProducts(search?: string): Observable<Product[]> {
    const url = search && search.trim()
      ? `/catalog/products?search=${encodeURIComponent(search.trim())}`
      : '/catalog/products';
    return this.http.get<Product[]>(url);
  }

  // -> gateway -> Catalog service
  addProduct(product: NewProduct): Observable<Product> {
    return this.http.post<Product>('/catalog/products', product);
  }

  // -> gateway -> Orders service
  getOrders(): Observable<Order[]> {
    return this.http.get<Order[]>('/orders/orders');
  }

  // -> gateway -> Orders service (which calls Catalog, then emits an event)
  placeOrder(cmd: CreateOrder): Observable<Order> {
    return this.http.post<Order>('/orders/orders', cmd);
  }

  // -> gateway -> Auth service (returns a signed JWT)
  login(username: string, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>('/auth/login', { username, password });
  }
}
