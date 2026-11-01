// Extend the checked-in final API template without replacing existing resources.
const fs = require('fs');
const path = require('path');
const file = path.join(__dirname, 'final-api.template.json');
const t = JSON.parse(fs.readFileSync(file, 'utf8'));
const ref = x => ({ Ref: x });
const sub = x => ({ 'Fn::Sub': x });
const get = (x, y) => ({ 'Fn::GetAtt': [x, y] });
const context = (why, must) => ({ 'com.aws.cloudformation.Context': { why, must } });
t.Resources.Groups.Properties.StreamSpecification = { StreamViewType: 'NEW_AND_OLD_IMAGES' };
t.Resources.NotificationFailureBucket = {
  Type: 'AWS::S3::Bucket',
  Metadata: context('Retains failed stream event payloads for recovery.', 'Private, encrypted, short retention; event payloads may contain personal data.'),
  Properties: {
    PublicAccessBlockConfiguration: { BlockPublicAcls: true, BlockPublicPolicy: true, IgnorePublicAcls: true, RestrictPublicBuckets: true },
    BucketEncryption: { ServerSideEncryptionConfiguration: [{ ServerSideEncryptionByDefault: { SSEAlgorithm: 'AES256' } }] },
    LifecycleConfiguration: { Rules: [{ Id: 'ExpireFailedEvents', Status: 'Enabled', ExpirationInDays: 7 }] }
  }
};
t.Resources.NotificationLogs = {
  Type: 'AWS::Logs::LogGroup', Metadata: context('Stores worker diagnostics without message contents.', 'Do not log email addresses, tokens, or stream payloads.'),
  Properties: { LogGroupName: sub('/aws/lambda/${AWS::StackName}-notification-worker'), RetentionInDays: 7 }
};
const email = t.Resources.ExecutionRole.Properties.Policies[0].PolicyDocument.Statement.find(s => s.Action?.includes('ses:SendEmail'));
if (!email) throw new Error('Existing scoped SES policy was not found');
t.Resources.NotificationRole = {
  Type: 'AWS::IAM::Role', Metadata: context('Permits the notification worker to read its stream, update its table and send scoped email.', 'Restrict storage, stream, logs, failure destination and SES resources.'),
  Properties: {
    AssumeRolePolicyDocument: { Version: '2012-10-17', Statement: [{ Effect: 'Allow', Principal: { Service: 'lambda.amazonaws.com' }, Action: 'sts:AssumeRole' }] },
    Policies: [{ PolicyName: 'NotificationWorker', PolicyDocument: { Version: '2012-10-17', Statement: [
      { Effect: 'Allow', Action: ['dynamodb:GetItem','dynamodb:PutItem','dynamodb:DeleteItem','dynamodb:ConditionCheckItem'], Resource: get('Groups','Arn') },
      { Effect: 'Allow', Action: ['dynamodb:DescribeStream','dynamodb:GetRecords','dynamodb:GetShardIterator'], Resource: get('Groups','StreamArn') },
      { Effect: 'Allow', Action: ['dynamodb:ListStreams'], Resource: '*' },
      { Effect: 'Allow', Action: ['logs:CreateLogStream','logs:PutLogEvents'], Resource: sub('arn:${AWS::Partition}:logs:${AWS::Region}:${AWS::AccountId}:log-group:/aws/lambda/${AWS::StackName}-notification-worker:*') },
      { Effect: 'Allow', Action: ['s3:PutObject'], Resource: sub('${NotificationFailureBucket.Arn}/*'), Condition: { StringEquals: { 's3:ResourceAccount': ref('AWS::AccountId') } } },
      { Effect: 'Allow', Action: ['s3:ListBucket'], Resource: get('NotificationFailureBucket','Arn'), Condition: { StringEquals: { 's3:ResourceAccount': ref('AWS::AccountId') } } },
      email
    ] } }]
  }
};
t.Resources.NotificationFunction = {
  Type: 'AWS::Lambda::Function', Metadata: context('Processes newly created notification intents from DynamoDB Streams.', 'No public invocation; reuse the same release package; never log stream images.'),
  Properties: {
    FunctionName: sub('${AWS::StackName}-notification-worker'), Runtime: 'dotnet8', Architectures: ['arm64'],
    Handler: 'Restaurant.Api::Restaurant.Api.NotificationStreamLambda::HandleAsync', Role: get('NotificationRole','Arn'),
    Code: { S3Bucket: ref('CodeBucket'), S3Key: ref('CodeKey') }, MemorySize: 512, Timeout: 60,
    Environment: { Variables: { DynamoDb__TableName: ref('Groups'), Email__Sender: ref('EmailSender'), Email__FrontendBaseUrl: ref('FrontendUrl') } }
  }, DependsOn: ['NotificationLogs']
};
t.Resources.NotificationMapping = {
  Type: 'AWS::Lambda::EventSourceMapping', Metadata: context('Delivers group changes to the worker with bounded retry and durable failure records.', 'Batch size one avoids oversized group images; retain failed event payloads for recovery.'),
  Properties: {
    EventSourceArn: get('Groups','StreamArn'), FunctionName: ref('NotificationFunction'), StartingPosition: 'TRIM_HORIZON',
    BatchSize: 1, Enabled: true, MaximumRetryAttempts: 20, MaximumRecordAgeInSeconds: 3600,
    DestinationConfig: { OnFailure: { Destination: get('NotificationFailureBucket','Arn') } }
  }
};
t.Resources.NotificationErrors = {
  Type: 'AWS::CloudWatch::Alarm', Metadata: context('Exposes worker errors in CloudWatch for investigation.', 'No email action configured; inspect alarm and retained failures.'),
  Properties: { AlarmDescription: 'Notification worker reported an error; inspect the failure store and delivery history.', Namespace: 'AWS/Lambda', MetricName: 'Errors', Statistic: 'Sum', Period: 300, EvaluationPeriods: 1, Threshold: 1, ComparisonOperator: 'GreaterThanOrEqualToThreshold', TreatMissingData: 'notBreaching', Dimensions: [{ Name: 'FunctionName', Value: ref('NotificationFunction') }] }
};
fs.writeFileSync(file, JSON.stringify(t, null, 2) + '\n');
