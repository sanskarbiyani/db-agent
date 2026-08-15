using Confluent.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;
using DbAgent.DbExecutor.Interfaces;
using DbAgent.DbExecutor.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace DbAgent.DbExecutor
{
    public class RetryChannelProcessor: BackgroundService
    {
        private static readonly int[] BackoffSeconds = { 2, 4, 8 };

        private readonly RetryChannel _retryChannel;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RetryChannelProcessor> _logger;
        private readonly IProducer<string, string> _producer;
        private readonly IDatabaseService _databaseService;

        public RetryChannelProcessor(
            RetryChannel retryChannel,
            IConfiguration configuration,
            ILogger<RetryChannelProcessor> logger,
            IDatabaseService databaseService)
        {
            _retryChannel = retryChannel;
            _configuration = configuration;
            _logger = logger;
            _databaseService = databaseService;

            var producerConfig = new Confluent.Kafka.ProducerConfig
            {
                BootstrapServers = _configuration["Kafka:BootstrapServers"]
            };

            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await foreach(var item in _retryChannel.RetryQueryChannel.Reader.ReadAllAsync(cancellationToken))
            {
               _ = ProcessRetryAsync(item, cancellationToken);
            }
        }

        private async Task ProcessRetryAsync(RetryMessage item, CancellationToken ct)
        {
            try
            {
                var delay = BackoffSeconds[item.AttemptNumber - 1];
                await Task.Delay(TimeSpan.FromSeconds(delay));

                var (result, category) = await _databaseService.ExecuteSqlAsync(item);

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "Execution successful for retry. ExecutionId: {ExecutionId}, AttemptNumber: {AttemptNumber}",
                        item.ExecutionId, item.AttemptNumber);
                }
                else
                {
                    _logger.LogWarning(
                        "Execution failed for retry. ExecutionId: {ExecutionId}, ErrorType: {ErrorType}, Category: {Category}, AttemptNumber: {AttemptNumber}",
                        item.ExecutionId, result.ErrorType, category, item.AttemptNumber);

                    await _databaseService.LogAttemptAsync(item.ExecutionId, item.AttemptNumber, result.ErrorType ?? "", result.ErrorMessage ?? "", false);

                    if(item.AttemptNumber < BackoffSeconds.Length)
                    {
                        item.AttemptNumber++;
                        await _retryChannel.RetryQueryChannel.Writer.WriteAsync(item, ct);
                    }
                    else
                    {
                        _logger.LogWarning("Query {QueryId} exhausted all retries — routing to failed-queries", item.ExecutionId);

                        // Not producing message to failed queries as it is a dead end.
                        // Instead we are updating the status in the database to indicate failure after all retries.
                        await _databaseService.UpdateFailedStatus(item.ExecutionId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown mid-delay — expected, nothing to log
            }
            catch (Exception ex) 
            {
                _logger.LogError(ex,
                "Unhandled error processing retry for query {QueryId}, attempt {Attempt}",
                item.ExecutionId, item.AttemptNumber);
            }
        }
    }
}
