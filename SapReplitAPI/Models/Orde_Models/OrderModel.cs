using System;
using System.Collections.Generic;

namespace SapReplitAPI.Models.Orde_Models
{
    public class OrderModel
    {
        public int DocEntry { get; set; }
        public int DocNum { get; set; }
        public string CustomerCode { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public DateTime DocDate { get; set; }
        public decimal OrderValue { get; set; }
        public string Status { get; set; } = string.Empty;

        public int SlpCode { get; set; }
        public string SlpName { get; set; } = string.Empty;

        public List<OrderLineModel> Lines { get; set; } = new List<OrderLineModel>();
    }
}