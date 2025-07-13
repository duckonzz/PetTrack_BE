using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Net.payOS;
using PetTrack.Contract.Repositories.Interfaces;
using PetTrack.Contract.Repositories.PaggingItems;
using PetTrack.Contract.Services.Interfaces;
using PetTrack.Core.Enums;
using PetTrack.Entity;
using PetTrack.ModelViews.TopUpModels;
using System.Reflection.Metadata;

namespace PetTrack.Services.Services
{
    public class TopUpTransactionService : ITopUpTransactionService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly PayOS _payOS;
        private readonly IWalletService _walletService;
        private readonly IMapper _mapper;
        private IUserContextService _userContextService;

        public TopUpTransactionService(IMapper mapper, IUnitOfWork unitOfWork, PayOS payOS, IWalletService walletService, IUserContextService userContextService)
        {
            _unitOfWork = unitOfWork;
            _payOS = payOS;
            _walletService = walletService;
            _userContextService = userContextService;
            _mapper = mapper;
        }

        public async Task CreateTopUpTransactionAsync(string accountId, decimal amount, string transactionCode,string? bookingId)
        {
            var transaction = new TopUpTransaction
            {
                UserId = accountId,
                Amount = amount,
                PaymentMethod = "PayOS",
                BookingId = bookingId == null ? null : bookingId,
                Status = TopUpTransactionStatus.Pending.ToString(),
                TransactionCode = transactionCode
            };
            _unitOfWork.GetRepository<TopUpTransaction>().Insert(transaction);
            await _unitOfWork.GetRepository<TopUpTransaction>().SaveAsync();
        }
        public async Task CheckStatusTransactionAsync(string transactionCode)
        {
            var transaction = await _unitOfWork.GetRepository<TopUpTransaction>()
                .Entities.FirstOrDefaultAsync(t => t.TransactionCode == transactionCode);
            if (transaction == null)
                throw new ArgumentException("Transaction not found");
            var checking = await _payOS.getPaymentLinkInformation(long.Parse(transactionCode));

            if (checking.status == "PAID")
            {
                var userId = _userContextService.GetUserId() ?? throw new ArgumentException("User not found", nameof(_userContextService));
                if (transaction.BookingId == null)
                {
                    var wallet = await _unitOfWork.GetRepository<Wallet>()
                    .Entities.FirstOrDefaultAsync(w => w.UserId == userId && !w.DeletedTime.HasValue);
                    await _walletService.AddBalanceAsync(wallet!.Id, transaction.Amount);

                }
                else
                {
                    // EXe sua lai cho dung voi booking
                    Booking? booking = await _unitOfWork.GetRepository<Booking>()
                    .Entities.Include(x => x.Clinic).FirstOrDefaultAsync(w => w.Id == transaction.BookingId && !w.DeletedTime.HasValue);
                    Wallet? wallet = await _unitOfWork.GetRepository<Wallet>()
                    .Entities.FirstOrDefaultAsync(w => w.UserId == booking.Clinic.OwnerUserId);
                    await _walletService.AddBalanceAsync(wallet.Id, booking.ClinicReceiveAmount ?? 0);

                    List<Booking> bookings = await _unitOfWork.GetRepository<Booking>()
                        .Entities.Where(b => b.UserId == booking.UserId).ToListAsync();

                    foreach (var b in bookings)
                    {
                        b.Status = BookingStatus.Completed.ToString();
                        _unitOfWork.GetRepository<Booking>().Update(b);
                    }
                    await _unitOfWork.GetRepository<Booking>().SaveAsync();
                }

                transaction.Status = TopUpTransactionStatus.Success.ToString();
                transaction.LastUpdatedTime = DateTimeOffset.UtcNow;
                _unitOfWork.GetRepository<TopUpTransaction>().Update(transaction);
                await _unitOfWork.GetRepository<TopUpTransaction>().SaveAsync();
            }
            else
            {
                throw new ArgumentException("Transaction is not paid yet");
            }

        }
       
        public async Task<PaginatedList<TopUpResponse>> GetTopUpTransaction(int pageIndex, int pageSize, string? userId = null, string? status = null)
        {
            var query = _unitOfWork.GetRepository<TopUpTransaction>().Entities.AsQueryable();

            if (string.IsNullOrEmpty(userId))
            {
                userId = _userContextService.GetUserId() ?? throw new ArgumentException("User not found", nameof(_userContextService));
            }
            
            query = query.Where(t => t.UserId == userId);

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(t => t.Status == status);
            }

            var pagedTransactions = await PaginatedList<TopUpTransaction>.CreateAsync(query, pageIndex, pageSize);

           
            var mapped = pagedTransactions.Items
                .Select(t => _mapper.Map<TopUpResponse>(t))
                .ToList();

            return new PaginatedList<TopUpResponse>(
                mapped,
                pagedTransactions.TotalCount,
                pagedTransactions.PageNumber,
                pagedTransactions.PageSize
            );
        }
    }
}
