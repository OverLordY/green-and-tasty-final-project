const fs = require('fs');
const path = require('path');
const file = path.join(__dirname, 'final-api.template.json');
const t = JSON.parse(fs.readFileSync(file, 'utf8'));
const ref = x => ({ Ref: x });
const sub = x => ({ 'Fn::Sub': x });
const get = (x,y) => ({ 'Fn::GetAtt': [x,y] });
const context = (why,must) => ({ 'com.aws.cloudformation.Context': { why,must } });
if (!t.Resources.NotificationFunction || !t.Resources.NotificationErrors) throw new Error('Notification worker infrastructure must be present.');
t.Resources.Function.Properties.Environment.Variables.GuestMail__Background = 'true';
t.Resources.NotificationAlarmLogs = {
  Type: 'AWS::Logs::LogGroup', Metadata: context('Stores alert delivery diagnostics.', 'Do not log email contents or private event data.'),
  Properties: { LogGroupName: sub('/aws/lambda/${AWS::StackName}-notification-alarm'), RetentionInDays: 7 }
};
t.Resources.NotificationAlarmRole = {
  Type: 'AWS::IAM::Role', Metadata: context('Sends an operational warning only to the configured sender.', 'SES must be limited to the sender identity; log writes limited to this function.'),
  Properties: { AssumeRolePolicyDocument: { Version: '2012-10-17', Statement: [{ Effect: 'Allow', Principal: { Service: 'lambda.amazonaws.com' }, Action: 'sts:AssumeRole' }] },
    Policies: [{ PolicyName: 'NotificationAlarm', PolicyDocument: { Version: '2012-10-17', Statement: [
      { Effect:'Allow', Action:['ses:SendEmail'], Resource:sub('arn:${AWS::Partition}:ses:${AWS::Region}:${AWS::AccountId}:identity/${EmailSender}'), Condition:{ StringEquals:{ 'ses:FromAddress':ref('EmailSender') }, 'ForAllValues:StringEquals':{ 'ses:Recipients':ref('EmailSender') }, Null:{ 'ses:Recipients':'false' } } },
      { Effect:'Allow', Action:['logs:CreateLogStream','logs:PutLogEvents'], Resource:sub('arn:${AWS::Partition}:logs:${AWS::Region}:${AWS::AccountId}:log-group:/aws/lambda/${AWS::StackName}-notification-alarm:*') }
    ] } }] }
};
t.Resources.NotificationAlarmFunction = {
  Type:'AWS::Lambda::Function', Metadata:context('Warns the project operator when the worker enters ALARM.', 'Send a static message only; avoid logging event data.'), DependsOn:['NotificationAlarmLogs'],
  Properties:{ FunctionName:sub('${AWS::StackName}-notification-alarm'), Runtime:'dotnet8', Architectures:['arm64'],
    Handler:'Restaurant.Api::Restaurant.Api.NotificationAlarmLambda::HandleAsync', Role:get('NotificationAlarmRole','Arn'),
    Code:{ S3Bucket:ref('CodeBucket'), S3Key:ref('CodeKey') }, MemorySize:256, Timeout:15,
    Environment:{ Variables:{ Email__Sender:ref('EmailSender') } } }
};
t.Resources.NotificationAlarmRule = {
  Type:'AWS::Events::Rule', Metadata:context('Routes the notification worker alarm to the operator mailer.', 'Only this alarm entering ALARM may trigger the function.'),
  Properties:{ State:'ENABLED', EventPattern:{ source:['aws.cloudwatch'], 'detail-type':['CloudWatch Alarm State Change'], resources:[get('NotificationErrors','Arn')], detail:{ state:{ value:['ALARM'] } } },
    Targets:[{ Id:'EmailOperator', Arn:get('NotificationAlarmFunction','Arn'), RetryPolicy:{ MaximumEventAgeInSeconds:3600, MaximumRetryAttempts:3 } }] }
};
t.Resources.NotificationAlarmPermission = { Type:'AWS::Lambda::Permission', Properties:{ Action:'lambda:InvokeFunction', FunctionName:ref('NotificationAlarmFunction'), Principal:'events.amazonaws.com', SourceArn:get('NotificationAlarmRule','Arn'), SourceAccount:ref('AWS::AccountId') } };
fs.writeFileSync(file, JSON.stringify(t, null, 2) + '\n');
