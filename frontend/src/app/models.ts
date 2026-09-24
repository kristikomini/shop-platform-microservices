export interface Product {
  id: number;
  name: string;
  price: number;
  stock: number;
}

export interface Order {
  id: string;
  productId: number;
  productName: string;
  unitPrice: number;
  quantity: number;
  total: number;
  placedAt: string;
}

export interface CreateOrder {
  productId: number;
  quantity: number;
}
