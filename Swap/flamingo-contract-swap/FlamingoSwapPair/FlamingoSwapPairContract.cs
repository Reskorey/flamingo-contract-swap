﻿using System;
using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;

namespace FlamingoSwapPair
{
    partial class FlamingoSwapPairContract : SmartContract
    {

        static readonly byte[] superAdmin = "AZaCs7GwthGy9fku2nFXtbrdKBRmrUQoFP".ToScriptHash();

        /// <summary>
        /// Token 0 地址
        /// </summary>
        static readonly byte[] Token0 = "1d8e292b5626acbf4b22633a8f1bbdc5b036ff1f".HexToBytes();

        /// <summary>
        ///  Token 1 地址
        /// </summary>
        static readonly byte[] Token1 = "902060e187aeff14b730d0e5eb5ce44d3b00f18a".HexToBytes();


        /// <summary>
        /// Factory地址
        /// </summary>
        static readonly byte[] FactoryContract = "00000000000".HexToBytes();

        private static readonly BigInteger MINIMUM_LIQUIDITY = 1000;


        /// <summary>
        /// 同步持有量Synced（reserve0，reserve1）
        /// </summary>
        private static event Action<BigInteger, BigInteger> Synced;

        /// <summary>
        /// 铸币事件 Minted(sender,amount0,amount1)
        /// </summary>
        private static event Action<byte[], BigInteger, BigInteger> Minted;

        /// <summary>
        /// 销毁事件 Burned(from,amount0,amount1,to)
        /// </summary>
        private static event Action<byte[], BigInteger, BigInteger, byte[]> Burned;

        /// <summary>
        /// 兑换事件
        /// </summary>
        public static event Action<byte[], BigInteger, BigInteger, BigInteger, BigInteger, byte[]> Swapped;





        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                return Runtime.CheckWitness(superAdmin);
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                var msgSender = ExecutionEngine.CallingScriptHash;//等价以太坊的msg.sender

                if (method == "balanceOf") return BalanceOf((byte[])args[0]);

                if (method == "decimals") return Decimals();

                if (method == "name") return Name();

                if (method == "symbol") return Symbol();

                if (method == "supportedStandards") return SupportedStandards();

                if (method == "totalSupply") return GetTotalSupply();

                if (method == "transfer") return Transfer((byte[])args[0], (byte[])args[1], (BigInteger)args[2], msgSender);

                if (method == "mint") return Mint(msgSender, (byte[])args[0]);

                if (method == "burn") return Burn(msgSender, (byte[])args[0]);

            }
            return false;
        }

        #region SyncUpdate


        /// <summary>
        /// 更新最新持有量（reserve）、价格累计量（price0CumulativeLast）、区块时间戳(blockTimestamp)
        /// </summary>
        /// <param name="balance0">最新的token0持有量</param>
        /// <param name="balance1">最新的token1持有量</param>
        /// <param name="reserve0"></param>
        /// <param name="reserve1"></param>
        public static void Update(BigInteger balance0, BigInteger balance1, BigInteger reserve0, BigInteger reserve1)
        {
            //todo:check???
            //require(balance0 <= uint112(-1) && balance1 <= uint112(-1), 'UniswapV2: OVERFLOW');

            var blockTimestamp = Runtime.Time;
            var blockTimestampLast = GetBlockTimestampLast();
            var timeElapsed = blockTimestamp - blockTimestampLast;
            if (timeElapsed > 0 && reserve0 != 0 && reserve1 != 0)
            {
                //todo:原始算法??
                //price0CumulativeLast += (total1 * Q112) / total0 * timeElapsed;
                // * never overflows, and + overflow is desired
                // price0CumulativeLast += uint(UQ112x112.encode(_reserve1).uqdiv(_reserve0)) * timeElapsed;
                // price1CumulativeLast += uint(UQ112x112.encode(_reserve0).uqdiv(_reserve1)) * timeElapsed;
                var price0CumulativeLast = GetPrice0CumulativeLast() + reserve1 / reserve0 * timeElapsed;
                var price1CumulativeLast = GetPrice1CumulativeLast() + reserve0 / reserve1 * timeElapsed;

                SetPrice0CumulativeLast(price0CumulativeLast);
                SetPrice1CumulativeLast(price1CumulativeLast);
            }

            SetReserve0(balance0);
            SetReserve1(balance1);
            SetBlockTimestampLast(blockTimestamp);
            Synced(balance0, balance1);
        }


        #endregion

        #region Swap

        /// <summary>
        /// 完成兑换
        /// </summary>
        /// <param name="amount0Out">已经计算好的token0 输出量</param>
        /// <param name="amount1Out">已经计算好的token1 输出量</param>
        /// <param name="from"></param>
        /// <param name="toAddress"></param>
        /// <param name="data"></param>
        public static void Swap(BigInteger amount0Out, BigInteger amount1Out, byte[] from, byte[] toAddress, byte[] data)
        {
            var me = ExecutionEngine.ExecutingScriptHash;
            //只少有一笔对外转出
            Assert(amount0Out > 0 || amount1Out > 0, "INSUFFICIENT_OUTPUT_AMOUNT");
            var reserve0 = GetReserve0();
            var reserve1 = GetReserve1();

            //转出量小于持有量
            Assert(amount0Out < reserve0 && amount1Out < reserve1, "INSUFFICIENT_LIQUIDITY");

            BigInteger balance0 = 0;
            BigInteger balance1 = 0;

            //禁止转到token本身的地址
            Assert(toAddress != Token0 && toAddress != Token1, "INVALID_TO");
            if (amount0Out > 0)
            {
                var tranferResult0 = DynamicTransfer(Token0, from, toAddress, amount0Out);
                Assert(tranferResult0, "Transfer Token0 Fail");
            }

            if (amount1Out > 0)
            {
                var tranferResult1 = DynamicTransfer(Token1, from, toAddress, amount1Out);
                Assert(tranferResult1, "Transfer Token1 Fail");
            }

            //???
            //if (data.length > 0) IUniswapV2Callee(to).uniswapV2Call(msg.sender, amount0Out, amount1Out, data);

            balance0 = DynamicBalanceOf(Token0, me);
            balance1 = DynamicBalanceOf(Token1, me);

            var amount0In = balance0 > (reserve0 - amount0Out) ? balance0 - (reserve0 - amount0Out) : 0;
            var amount1In = balance1 > (reserve1 - amount1Out) ? balance1 - (reserve1 - amount1Out) : 0;

            Assert(amount0In > 0 || amount1In > 0, "INSUFFICIENT_INPUT_AMOUNT");

            var balance0Adjusted = balance0 * 1000 - amount0In * 3;
            var balance1Adjusted = balance1 * 1000 - amount1In * 3;

            Assert(balance0Adjusted * balance1Adjusted >= reserve0 * reserve1 * 1_000_000, "K");

            Update(balance0, balance1, reserve0, reserve1);

            Swapped(from, amount0In, amount1In, amount0Out, amount1Out, toAddress);
        }


        #endregion

        #region Burn and Mint

        /// <summary>
        /// 销毁代币
        /// </summary>
        /// <param name="from"></param>
        /// <param name="toAddress"></param>
        /// <returns></returns>
        public static object Burn(byte[] from, byte[] toAddress)
        {
            var me = ExecutionEngine.ExecutingScriptHash;
            var reserve0 = GetReserve0();
            var reserve1 = GetReserve1();

            var balance0 = DynamicBalanceOf(Token0, me);
            var balance1 = DynamicBalanceOf(Token1, me);
            var liquidity = BalanceOf(me);

            bool feeOn = MintFee(reserve0, reserve1);
            var totalSupply = GetTotalSupply();
            var amount0 = liquidity * balance0 / totalSupply;//要销毁(转出)的token0额度：me持有的token0 * (me持有的me token/me token总量）
            var amount1 = liquidity * balance1 / totalSupply;

            Assert(amount0 > 0 && amount1 > 0, "INSUFFICIENT_LIQUIDITY_BURNED");
            BurnToken(me, liquidity);

            var transfer1 = DynamicTransfer(Token0, from, toAddress, amount0);
            Assert(transfer1, "transfer1 fail");
            var transfer2 = DynamicTransfer(Token1, from, toAddress, amount1);
            Assert(transfer2, "transfer2 fail");

            balance0 = DynamicBalanceOf(Token0, me);
            balance1 = DynamicBalanceOf(Token1, me);

            Update(balance0, balance1, reserve0, reserve1);

            if (feeOn)
            {
                var kLast = reserve0 * reserve1;
                SetKLast(kLast);
            }
            Burned(from, amount0, amount1, toAddress);
            //if (feeOn) kLast = uint(reserve0).mul(reserve1); // reserve0 and reserve1 are up-to-date
            //emit Burn(msg.sender, amount0, amount1, to);
            return new BigInteger[]
            {
                amount0,
                amount1,
            };
        }


        /// <summary>
        /// 铸造代币
        /// </summary>
        /// <param name="from"></param>
        /// <param name="toAddress"></param>
        /// <returns></returns>
        public static BigInteger Mint(byte[] from, byte[] toAddress)
        {
            var me = ExecutionEngine.ExecutingScriptHash;

            var reserve0 = GetReserve0();
            var reserve1 = GetReserve1();
            var balance0 = DynamicBalanceOf(Token0, me);
            var balance1 = DynamicBalanceOf(Token1, me);

            var amount0 = balance0 - reserve0;//token0增量
            var amount1 = balance1 - reserve1;//token1增量



            bool feeOn = MintFee(reserve0, reserve1);
            var totalSupply = GetTotalSupply();
            BigInteger liquidity;
            if (totalSupply == 0)
            {
                liquidity = Sqrt(amount0 * amount1) - MINIMUM_LIQUIDITY;
                MintToken(new byte[20], MINIMUM_LIQUIDITY);// permanently lock the first MINIMUM_LIQUIDITY tokens,永久锁住第一波发行的 MINIMUM_LIQUIDITY token
            }
            else
            {
                var liquidity0 = amount0 * totalSupply / reserve0;
                var liquidity1 = amount1 * totalSupply / reserve1;
                liquidity = liquidity0 > liquidity1 ? liquidity1 : liquidity0;
            }

            Assert(liquidity > 0, "INSUFFICIENT_LIQUIDITY_MINTED");
            MintToken(toAddress, liquidity);

            Update(balance0, balance1, reserve0, reserve1);
            if (feeOn)
            {
                var kLast = reserve0 * reserve1;
                SetKLast(kLast);
            }

            Minted(from, amount0, amount1);
            return liquidity;
        }




        /// <summary>
        /// 从Factory获取手续费收益地址
        /// </summary>
        /// <returns></returns>
        private static byte[] GetFeeTo()
        {
            var factoryCall = (Func<string, object[], byte[]>)FactoryContract.ToDelegate();
            var feeTo = factoryCall("getFeeTo", new object[0]);
            return feeTo;
        }

        /// <summary>
        /// if fee is on, mint liquidity equivalent to 1/6th of the growth in sqrt(k)
        /// 发放铸币fee给收益地址（目前没在使用）
        /// </summary>
        /// <param name="reserve0">当前token0持有量</param>
        /// <param name="reserve1">当前token1持有量</param>
        public static bool MintFee(BigInteger reserve0, BigInteger reserve1)
        {
            byte[] feeTo = GetFeeTo();
            bool feeOn = feeTo.Length == 20;
            var kLast = GetKLast();
            if (feeOn)
            {
                if (kLast != 0)
                {
                    var rootK = Sqrt(reserve0 * reserve1);
                    var rootKLast = Sqrt(kLast);
                    if (rootK > rootKLast)
                    {
                        //如果资金池变大
                        var numerator = Sqrt(GetTotalSupply() * (rootK - rootKLast));
                        var denominator = (rootK * 5) + rootKLast;
                        var liquidity = numerator / denominator;
                        if (liquidity > 0)
                        {
                            MintToken(feeTo, liquidity);
                        }
                    }
                }
            }
            else if (kLast != 0)
            {
                SetKLast(0);
            }

            return feeOn;
        }


        #endregion

        #region Reserve读写

        /// <summary>
        /// 获取本合约持有的Token0总量(由本合约记录)
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetReserve0()
        {
            return Storage.Get(Token0).AsBigInteger();
        }


        /// <summary>
        /// 设置本合约持有的Token0总量(由本合约记录)
        /// </summary>
        /// <param name="newReserve"></param>
        /// <returns></returns>
        private static bool SetReserve0(BigInteger newReserve)
        {
            Storage.Put(Token0, newReserve);
            return true;
        }


        /// <summary>
        /// 获取本合约持有的Token1总量(由本合约记录)
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetReserve1()
        {
            return Storage.Get(Token1).AsBigInteger();
        }


        /// <summary>
        /// 设置本合约持有的Token1总量(由本合约记录)
        /// </summary>
        /// <param name="newReserve"></param>
        /// <returns></returns>
        private static bool SetReserve1(BigInteger newReserve)
        {
            Storage.Put(Token1, newReserve);
            return true;
        }

        #endregion

        #region BlockTimestampLast更新时间戳


        /// <summary>
        /// 最后一次更新的区块时间戳存储Key
        /// </summary>
        private const string BlockTimestampLastStoreKey = "BlockTimestampLast";


        /// <summary>
        /// 获取最后一次更新的区块时间戳
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetBlockTimestampLast()
        {
            return Storage.Get(BlockTimestampLastStoreKey).AsBigInteger();
        }


        /// <summary>
        /// 记录最后一次更新的区块时间戳
        /// </summary>
        /// <param name="height"></param>
        /// <returns></returns>
        private static bool SetBlockTimestampLast(BigInteger height)
        {
            Storage.Put(BlockTimestampLastStoreKey, height);
            return true;
        }

        #endregion

        #region PriceCumulativeLast累计价格


        /// <summary>
        /// 累计价格存储Key
        /// </summary>
        private const string Price0CumulativeLastStoreKey = "Price0CumulativeLast";
        private const string Price1StoreKey = "Price1CumulativeLast";


        /// <summary>
        /// 获取token0累计价格
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetPrice0CumulativeLast()
        {
            return Storage.Get(Price0CumulativeLastStoreKey).AsBigInteger();
        }


        /// <summary>
        /// 设置token0累计价格
        /// </summary>
        /// <param name="price0CumulativeLast"></param>
        /// <returns></returns>
        private static bool SetPrice0CumulativeLast(BigInteger price0CumulativeLast)
        {
            Storage.Put(Price0CumulativeLastStoreKey, price0CumulativeLast);
            return true;
        }



        /// <summary>
        /// 获取token1累计价格
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetPrice1CumulativeLast()
        {
            return Storage.Get(Price1StoreKey).AsBigInteger();
        }


        /// <summary>
        /// 设置token1累计价格
        /// </summary>
        /// <param name="price1CumulativeLast"></param>
        /// <returns></returns>
        private static bool SetPrice1CumulativeLast(BigInteger price1CumulativeLast)
        {
            Storage.Put(Price1StoreKey, price1CumulativeLast);
            return true;
        }

        #endregion

        #region K值


        /// <summary>
        /// 获取记录的KLast（reserve0 * reserve1,恒定积）
        /// </summary>
        /// <returns></returns>
        private static BigInteger GetKLast()
        {
            return Storage.Get("KLast").AsBigInteger();
        }


        /// <summary>
        /// 记录的KLast(reserve0 * reserve1,恒定积)
        /// </summary>
        /// <param name="kLast"></param>
        /// <returns></returns>
        private static bool SetKLast(BigInteger kLast)
        {
            Storage.Put("KLast", kLast);
            return true;
        }

        #endregion
    }
}
