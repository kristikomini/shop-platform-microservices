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
  status: string;
}

export interface NewProduct {
  name: string;
  price: number;
  stock: number;
}

export interface CreateOrder {
  productId: number;
  quantity: number;
}
