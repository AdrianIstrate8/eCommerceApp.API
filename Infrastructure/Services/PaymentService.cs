using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;
using Core.Specifications;
using Microsoft.Extensions.Configuration;
using Stripe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Order = Core.Entities.OrderAggregate.Order;
using Product = Core.Entities.Product;

namespace Infrastructure.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly IBasketRepository _basketRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IConfiguration _config;

        public PaymentService(IBasketRepository basketRepository, IUnitOfWork unitOfWork,
            IConfiguration config)
        {
            _basketRepository = basketRepository;
            _unitOfWork = unitOfWork;
            _config = config;
        }

        public async Task<CustomerBasket> CreateOrUpdatePaymentIntent(string basketId)
        {
            //get the stripe secret key from appsettings
            StripeConfiguration.ApiKey = _config["StripeSettings:SecretKey"];

            //return the content of the basket from db
            var basket = await _basketRepository.GetBasketAsync(basketId);

            //check to see that we have a basket
            if (basket == null) return null;

            //initialize the shipping cost with 0
            var shippingPrice = 0m;

            //check to see if the current basket structure has set a delivery id
            if (basket.DeliveryMethodId.HasValue)
            {
                //if the delivery method has been set, retrieve the full delivery information
                var deliveryMethod = await _unitOfWork.Repository<DeliveryMethod>().GetByIdAsync((int)basket.DeliveryMethodId);

                //set the current shipping price with the price of selected delivery method
                shippingPrice = deliveryMethod.Price;
            }

            //check de items from basket and confirm that the prices stored there are accurate
            foreach (var item in basket.Items)
            {
                //get the product from db
                var productItem = await _unitOfWork.Repository<Product>().GetByIdAsync(item.Id);

                //check the price from db with what the client had in the basket
                if(item.Price != productItem.Price)
                {
                    //set the basket item price in case for some reason the client does not have the same price for the item
                    item.Price = productItem.Price;
                }
            }
            
            //initialize the PaymentIntent Service
            var service = new PaymentIntentService();

            //initialize the PaymentIntent Class
            PaymentIntent intent;

            //check to see if the client already made a payment intent
            if (string.IsNullOrEmpty(basket.PaymentIntentId))
            {
                //if not, create a new payment intent
                var options = new PaymentIntentCreateOptions
                {
                    Amount = (long)basket.Items.Sum(i => i.Quantity * (i.Price * 100)) + (long)shippingPrice * 100,
                    Currency = "usd",
                    PaymentMethodTypes = new List<string> { "card" }
                };
                intent = await service.CreateAsync(options);

                //return and set the PaymentIntentId and ClientSecret
                basket.PaymentIntentId = intent.Id;
                basket.ClientSecret = intent.ClientSecret;
            }
            else
            {
                //if yes, just update the payment amount
                var options = new PaymentIntentUpdateOptions
                {
                    Amount = (long)basket.Items.Sum(i => i.Quantity * (i.Price * 100)) + (long)shippingPrice * 100,
                };
                await service.UpdateAsync(basket.PaymentIntentId, options);
            }

            //update the basket content in db with the new content
            await _basketRepository.UpdateBasketAsync(basket);

            return basket;
        }

        public async Task<Order> UpdateOrderPaymentFailed(string paymentIntentId)
        {
            var spec = new OrderByPaymentIntentIdSpecification(paymentIntentId);
            var order = await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

            if (order == null) return null;

            order.Status = OrderStatus.PaymentFailed;
            await _unitOfWork.Complete();

            return order;
        }

        public async Task<Order> UpdateOrderPaymentSucceeded(string paymentIntentId)
        {
            var spec = new OrderByPaymentIntentIdSpecification(paymentIntentId);
            var order = await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

            if (order == null) return null;

            order.Status = OrderStatus.PaymentReceived;
            _unitOfWork.Repository<Order>().Update(order);

            await _unitOfWork.Complete();

            return order;
        }
    }
}
