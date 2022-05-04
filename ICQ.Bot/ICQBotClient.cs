﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using ICQ.Bot.Args;
using ICQ.Bot.Converters;
using ICQ.Bot.Exceptions;
using ICQ.Bot.Requests;
using ICQ.Bot.Requests.Abstractions;
using ICQ.Bot.Types;
using ICQ.Bot.Types.Enums;
using ICQ.Bot.Types.InputFiles;
using ICQ.Bot.Types.ReplyMarkups;

using Newtonsoft.Json;


namespace ICQ.Bot
{
    //https://stackoverflow.com/questions/3526701/how-to-generate-a-class-from-an-interface
    //https://icq.com/botapi/#/files/get_files_getInfo
    public class ICQBotClient : IICQBotClient
    {

        private static readonly Updates EmptyUpdates = new Updates();

        private const string baseUrl = "https://api.icq.net/bot/v1";

        private readonly string _token;

        private readonly HttpClient _httpClient;

        private readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(1);

        public TimeSpan Timeout
        {
            get => _httpClient.Timeout.Subtract(TimeSpan.FromSeconds(5));
        }

        public bool IsReceiving { get; set; }

        private CancellationTokenSource _receivingCancellationTokenSource;
        /// <summary>
        /// Id of the last known (processed) event.
        /// </summary>
        public int MessageOffset { get; set; }

        public ICQBotClient(string token, HttpClient httpClient = null)
        {
            _token = token;
            _httpClient = httpClient ?? new HttpClient
            {
                Timeout = HttpClientTimeout
            };
        }

        public ICQBotClient(string token, IWebProxy webProxy)
        {
            _token = token;
            var httpClientHander = new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true
            };
            _httpClient = new HttpClient(httpClientHander)
            {
                Timeout = HttpClientTimeout
            };
        }

        public event EventHandler<ApiRequestEventArgs> MakingApiRequest;
        public event EventHandler<ApiResponseEventArgs> ApiResponseReceived;
        public event EventHandler<UpdateEventArgs> OnUpdate;
        public event EventHandler<MessageEventArgs> OnMessage;
        public event EventHandler<MessageEventArgs> OnMessageEdited;
        public event EventHandler<CallbackQueryEventArgs> OnCallbackQuery;
        public event EventHandler<ReceiveErrorEventArgs> OnReceiveError;
        public event EventHandler<ReceiveGeneralErrorEventArgs> OnReceiveGeneralError;

        public Task<Updates> GetUpdatesAsync(
            int offset = default,
            int limit = default,
            int timeout = default,
            IEnumerable<Types.Enums.UpdateType> allowedUpdates = default,
            CancellationToken cancellationToken = default
        ) => MakeRequestAsync(new GetUpdatesRequest(offset, timeout), cancellationToken);

        public Task<MessagesResponse> SendTextMessageAsync(
            ChatId chatId,
            string text,
            bool disableWebPagePreview = default,
            bool disableNotification = default,
            long replyToMessageId = default,
            IReplyMarkup replyMarkup = default,
            CancellationToken cancellationToken = default,
            ParseMode parsedMode = ParseMode.MarkdownV2
        ) => MakeRequestAsync(new SendMessageRequest(chatId, text)
                {
                    ReplyMarkup = replyMarkup,
                    DisableWebPagePreview = disableWebPagePreview,
                    DisableNotification = disableNotification,
                    ReplyToMessageId = replyToMessageId,
                    ParseMode = parsedMode
                }, cancellationToken);

        public Task<MessagesResponse> EditMessageTextAsync(
            ChatId chatId,
            long messageId,
            string text,
            bool disableWebPagePreview = default,
            InlineKeyboardMarkup replyMarkup = default,            
            CancellationToken cancellationToken = default,
            ParseMode parsedMode = ParseMode.MarkdownV2
        ) => MakeRequestAsync(new EditMessageTextRequest(chatId, messageId, text)
                {
                    ReplyMarkup = replyMarkup,
                    DisableWebPagePreview = disableWebPagePreview,
                    ParseMode = parsedMode
        }, cancellationToken);

        public Task AnswerCallbackQueryAsync(
            string callbackQueryId,
            string text = default,
            bool showAlert = default,
            string url = default,
            int cacheTime = default,
            CancellationToken cancellationToken = default
        ) => MakeRequestAsync(new AnswerCallbackQueryRequest(callbackQueryId)
                {
                    Url = url,
                    Text = text,
                    ShowAlert = showAlert,
                    CacheTime = cacheTime
                }, cancellationToken);

        public Task<MessagesResponse> SendFileAsync(
            ChatId chatId,
            InputOnlineFile document,
            string caption = default,
            bool disableNotification = default,
            long replyToMessageId = default,
            IReplyMarkup replyMarkup = default,
            InputMedia thumb = default,
            CancellationToken cancellationToken = default,
            ParseMode parsedMode = ParseMode.MarkdownV2
        ) => ProcessSendFileRequestAsync(chatId, document, caption, disableNotification, replyToMessageId, replyMarkup, thumb, cancellationToken, parsedMode);

        public Task KickChatMemberAsync(
            ChatId chatId,
            int userId,
            DateTime untilDate = default,
            CancellationToken cancellationToken = default
        ) => MakeRequestAsync(new KickChatMemberRequest(chatId, userId)
                {
                    UntilDate = untilDate
                }, cancellationToken);

        public Task<User> GetMeAsync(CancellationToken cancellationToken = default)
            => MakeRequestAsync(new GetMeRequest(), cancellationToken);

        public Task UnbanChatMemberAsync(ChatId chatId, int userId, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new UnbanChatMemberRequest(chatId, userId), cancellationToken);

        public Task<ChatAdmins> GetChatAdministratorsAsync(ChatId chatId, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new GetChatAdministratorsRequest(chatId), cancellationToken);

        public Task<ChatInfo> GetChatAsync(ChatId chatId, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new GetChatRequest(chatId), cancellationToken);

        public Task<Types.File> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new GetFileInfoRequest(fileId), cancellationToken);

        public Task SendChatActionsAsync(ChatId chatId, ChatAction chatAction, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new SendChatActionsRequest(chatId, chatAction), cancellationToken);

        public Task DeleteMessageAsync(ChatId chatId, IEnumerable<long> messageIds, CancellationToken cancellationToken = default)
            => MakeRequestAsync(new DeleteMessagesRequest(chatId, messageIds), cancellationToken);

        public void StartReceiving(Types.Enums.UpdateType[] allowedUpdates = null, CancellationToken cancellationToken = default)
        {
            _receivingCancellationTokenSource = new CancellationTokenSource();
            cancellationToken.Register(() => _receivingCancellationTokenSource.Cancel());
            ReceiveAsync(allowedUpdates, _receivingCancellationTokenSource.Token);
        }

        private async void ReceiveAsync(
           Types.Enums.UpdateType[] allowedUpdates,
           CancellationToken cancellationToken = default)
        {
            IsReceiving = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var timeout = Convert.ToInt32(Timeout.TotalSeconds);
                var updates = EmptyUpdates;

                try
                {
                    updates = await GetUpdatesAsync(
                        MessageOffset,
                        timeout: timeout,
                        allowedUpdates: allowedUpdates,
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ApiRequestException apiException)
                {
                    OnReceiveError?.Invoke(this, apiException);
                }
                catch (Exception generalException)
                {
                    OnReceiveGeneralError?.Invoke(this, generalException);
                }

                try
                {
                    if (updates.Events != null)
                    {
                        foreach (var update in updates.Events)
                        {
                            OnUpdateReceived(new UpdateEventArgs(update));
                            MessageOffset = update.EventId;
                        }                        
                    }
                }
                catch
                {
                    IsReceiving = false;
                    throw;
                }
            }

            IsReceiving = false;
        }

        public void StopReceiving()
        {
            try
            {
                _receivingCancellationTokenSource.Cancel();
            }
            catch (WebException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        public async Task<bool> TestApiAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await GetMeAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (ApiRequestException e)
                when (e.ErrorCode == 401)
            {
                return false;
            }
        }

        protected virtual void OnUpdateReceived(UpdateEventArgs e)
        {
            OnUpdate?.Invoke(this, e);

            switch (e.Update.Type)
            {
                case "newMessage":
                    OnMessage?.Invoke(this, e);
                    break;

                case "callbackQuery":
                    OnCallbackQuery?.Invoke(this, e);
                    break;

                case "editedMessage":
                    OnMessageEdited?.Invoke(this, e);
                    break;
            }
        }
        
        private Task<MessagesResponse> ProcessSendFileRequestAsync(ChatId chatId, InputOnlineFile document, string caption, bool disableNotification, long replyToMessageId, IReplyMarkup replyMarkup, InputMedia thumb, CancellationToken cancellationToken, ParseMode parseMode)
        {
            if (document == null)
            {
                throw new InvalidParameterException(nameof(document));
            }

            IRequest<MessagesResponse> request;
            if (document.Content != null)
            {
                request = new SendFilePostRequest(chatId, document)
                {
                    Caption = caption,
                    ReplyMarkup = replyMarkup,
                    Thumb = thumb,
                    DisableNotification = disableNotification,
                    ReplyToMessageId = replyToMessageId,
                    ParseMode = parseMode
                };
            }
            else
            {
                request = new SendFileGetRequest(chatId, document)
                {
                    Caption = caption,
                    ReplyMarkup = replyMarkup,
                    Thumb = thumb,
                    DisableNotification = disableNotification,
                    ReplyToMessageId = replyToMessageId
                };
            }

            return MakeRequestAsync(request, cancellationToken);
        }

        private async Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            string url = string.Format("{0}{1}", baseUrl, request.MethodName);
            HttpContent httpContent = request.ToHttpContent();
            var httpRequest = new HttpRequestMessage(request.Method, url)
            {
                Content = httpContent,
            };

            var reqDataArgs = new ApiRequestEventArgs
            {
                MethodName = request.MethodName,
                HttpContent = httpRequest.Content,
            };
            MakingApiRequest?.Invoke(this, reqDataArgs);

            HttpResponseMessage httpResponse;
            try
            {
                string queryString;
                if (httpContent != null)
                {
                    string prefix = await httpContent.ReadAsStringAsync().ConfigureAwait(false);
                    prefix = HttpUtility.UrlDecode(prefix);
                    queryString = string.Format("{0}&token={1}", prefix, _token);
                }
                else
                {
                    queryString = string.Format("token={0}", _token);
                }

                url = string.Format("{0}?{1}", url, queryString);
                if (request.Method == HttpMethod.Get)
                {
                    httpResponse = await _httpClient.GetAsync(url).ConfigureAwait(false);
                }
                else if (request.Method == HttpMethod.Post)
                {
                    HttpContent newEncodedContent = CreateContent(request);
                    httpResponse = await _httpClient.PostAsync(url, newEncodedContent ?? httpContent).ConfigureAwait(false);
                }
                else
                {
                    throw new ApiRequestException(string.Format("invalid request method: {0}", request.Method.Method));
                }
            }
            catch (TaskCanceledException e)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new ApiRequestException("request timed out", 408, e);
            }

            // required since user might be able to set new status code using following event arg
            var actualResponseStatusCode = httpResponse.StatusCode;
            string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            ApiResponseReceived?.Invoke(this, new ApiResponseEventArgs
            {
                ResponseMessage = httpResponse,
                ApiRequestEventArgs = reqDataArgs
            });

            switch (actualResponseStatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.BadRequest when !string.IsNullOrWhiteSpace(responseJson):
                case HttpStatusCode.Forbidden when !string.IsNullOrWhiteSpace(responseJson):
                case HttpStatusCode.Conflict when !string.IsNullOrWhiteSpace(responseJson):
                    // Do NOT throw here, an ApiRequestException will be thrown next
                    break;
                default:
                    httpResponse.EnsureSuccessStatusCode();
                    break;
            }
            var apiResponse = JsonConvert.DeserializeObject<TResponse>(responseJson, new DateTimeConverter());
            if (apiResponse == null)
            {
                string text = string.Format("response failed to be parsed");
                throw new ApiRequestException(text);
            }

            return apiResponse;
        }

        private HttpContent CreateContent<TResponse>(IRequest<TResponse> request)
        {
            HttpContent result = null;
            if (request is SendFilePostRequest)
            {
                var sendFileRequest = (request as SendFilePostRequest);
                if (sendFileRequest.Document == null || sendFileRequest.Document.Content == null)
                {
                    throw new FileStreamNotFoundException();
                }

                result = sendFileRequest.ToMultipartFormDataContent("file", sendFileRequest.Document);
            }

            return result;
        }
    }
}
